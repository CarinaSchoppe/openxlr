// SPDX-License-Identifier: GPL-3.0-only
// One isolated VST3 module scanner or real-time PipeWire DSP instance.
// Third-party code is never loaded into the OpenXLR daemon itself.
#include "pluginterfaces/base/funknown.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/vstspeaker.h"
#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "public.sdk/source/vst/utility/stringconvert.h"

#include <X11/Xatom.h>
#include <X11/Xlib.h>
#include <fcntl.h>
#include <pipewire/filter.h>
#include <pipewire/pipewire.h>
#include <sys/prctl.h>
#include <unistd.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <memory>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <vector>

using Steinberg::FUnknown;
using Steinberg::IPtr;
using Steinberg::MemoryStream;
using Steinberg::tresult;
using Steinberg::Vst::BusInfo;
using Steinberg::Vst::HostProcessData;
using Steinberg::Vst::IComponent;
using Steinberg::Vst::IComponentHandler;
using Steinberg::Vst::IEditController;
using Steinberg::Vst::IAudioProcessor;
using Steinberg::Vst::IParamValueQueue;
using Steinberg::Vst::ParameterChanges;
using Steinberg::Vst::ParameterInfo;
using Steinberg::Vst::ParamID;
using Steinberg::Vst::ParamValue;

namespace {

constexpr uint32_t kMaxFrames = 8192;
constexpr size_t kMaxCommandBytes = 1024 * 1024;
constexpr size_t kMaxStateBytes = 512 * 1024;
constexpr int kMaxParameters = 4096;
constexpr uint32_t kStateMagic = 0x32534c58; // "XLS2"

std::string json_escape(std::string_view value) {
  std::ostringstream out;
  for (unsigned char c : value) {
    switch (c) {
    case '\\': out << "\\\\"; break;
    case '"': out << "\\\""; break;
    case '\b': out << "\\b"; break;
    case '\f': out << "\\f"; break;
    case '\n': out << "\\n"; break;
    case '\r': out << "\\r"; break;
    case '\t': out << "\\t"; break;
    default:
      if (c < 0x20) {
        constexpr char hex[] = "0123456789abcdef";
        out << "\\u00" << hex[c >> 4] << hex[c & 15];
      } else {
        out << c;
      }
    }
  }
  return out.str();
}

std::string text(const Steinberg::Vst::TChar *value) {
  return Steinberg::Vst::StringConvert::convert(value, 128);
}

std::string base64_encode(const uint8_t *data, size_t size) {
  constexpr char alphabet[] =
      "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  std::string result;
  result.reserve((size + 2) / 3 * 4);
  for (size_t offset = 0; offset < size; offset += 3) {
    uint32_t value = static_cast<uint32_t>(data[offset]) << 16;
    if (offset + 1 < size) value |= static_cast<uint32_t>(data[offset + 1]) << 8;
    if (offset + 2 < size) value |= data[offset + 2];
    result.push_back(alphabet[(value >> 18) & 63]);
    result.push_back(alphabet[(value >> 12) & 63]);
    result.push_back(offset + 1 < size ? alphabet[(value >> 6) & 63] : '=');
    result.push_back(offset + 2 < size ? alphabet[value & 63] : '=');
  }
  return result;
}

std::optional<std::vector<uint8_t>> base64_decode(std::string_view value) {
  if (value.size() % 4 != 0 || value.size() > (kMaxStateBytes + 16) * 4 / 3 + 8)
    return std::nullopt;
  std::array<int8_t, 256> decode{};
  decode.fill(-1);
  constexpr char alphabet[] =
      "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  for (int i = 0; i < 64; ++i)
    decode[static_cast<unsigned char>(alphabet[i])] = static_cast<int8_t>(i);
  std::vector<uint8_t> result;
  result.reserve(value.size() / 4 * 3);
  for (size_t offset = 0; offset < value.size(); offset += 4) {
    uint32_t bits = 0;
    int padding = 0;
    for (int index = 0; index < 4; ++index) {
      unsigned char c = static_cast<unsigned char>(value[offset + index]);
      if (c == '=') {
        if (index < 2) return std::nullopt;
        ++padding;
        bits <<= 6;
      } else {
        if (padding || decode[c] < 0) return std::nullopt;
        bits = (bits << 6) | static_cast<uint8_t>(decode[c]);
      }
    }
    result.push_back(static_cast<uint8_t>(bits >> 16));
    if (padding < 2) result.push_back(static_cast<uint8_t>(bits >> 8));
    if (padding < 1) result.push_back(static_cast<uint8_t>(bits));
  }
  if (result.size() > kMaxStateBytes) return std::nullopt;
  return result;
}

void append_u32(std::vector<uint8_t> &data, uint32_t value) {
  for (int shift = 0; shift < 32; shift += 8)
    data.push_back(static_cast<uint8_t>(value >> shift));
}

void append_u64(std::vector<uint8_t> &data, uint64_t value) {
  for (int shift = 0; shift < 64; shift += 8)
    data.push_back(static_cast<uint8_t>(value >> shift));
}

std::optional<uint32_t> read_u32(const std::vector<uint8_t> &data, size_t &offset) {
  if (offset + 4 > data.size()) return std::nullopt;
  uint32_t value = 0;
  for (int shift = 0; shift < 32; shift += 8)
    value |= static_cast<uint32_t>(data[offset++]) << shift;
  return value;
}

std::optional<uint64_t> read_u64(const std::vector<uint8_t> &data, size_t &offset) {
  if (offset + 8 > data.size()) return std::nullopt;
  uint64_t value = 0;
  for (int shift = 0; shift < 64; shift += 8)
    value |= static_cast<uint64_t>(data[offset++]) << shift;
  return value;
}

void print_parameter(IEditController &controller, const ParameterInfo &info) {
  const double minimum = controller.normalizedParamToPlain(info.id, 0.0);
  const double maximum = controller.normalizedParamToPlain(info.id, 1.0);
  const double initial =
      controller.normalizedParamToPlain(info.id, info.defaultNormalizedValue);
  std::cout << "{\"symbol\":\"" << info.id << "\",\"name\":\""
            << json_escape(text(info.title)) << "\",\"min\":" << minimum
            << ",\"max\":" << maximum << ",\"default\":" << initial
            << ",\"toggled\":" << (info.stepCount == 1 ? "true" : "false")
            << ",\"integer\":" << (info.stepCount > 1 ? "true" : "false")
            << ",\"logarithmic\":false,\"enumeration\":"
            << (info.stepCount > 1 ? "true" : "false")
            << ",\"scalePoints\":[";
  const int steps = std::min(info.stepCount, 128);
  for (int step = 0; step <= steps && info.stepCount > 1; ++step) {
    if (step) std::cout << ',';
    const ParamValue normalized = static_cast<double>(step) / info.stepCount;
    Steinberg::Vst::String128 display{};
    controller.getParamStringByValue(info.id, normalized, display);
    std::cout << "{\"label\":\"" << json_escape(text(display))
              << "\",\"value\":"
              << controller.normalizedParamToPlain(info.id, normalized) << '}';
  }
  std::cout << "],\"unit\":\"" << json_escape(text(info.units)) << "\"}";
}

void print_aux_buses(IComponent &component) {
  bool first = true;
  const int count = component.getBusCount(Steinberg::Vst::kAudio,
                                           Steinberg::Vst::kInput);
  for (int index = 0; index < count; ++index) {
    BusInfo info{};
    if (component.getBusInfo(Steinberg::Vst::kAudio, Steinberg::Vst::kInput,
                             index, info) != Steinberg::kResultOk ||
        info.busType != Steinberg::Vst::kAux)
      continue;
    if (!first) std::cout << ',';
    first = false;
    std::cout << "{\"id\":\"aux-" << index << "\",\"name\":\""
              << json_escape(text(info.name)) << "\",\"channels\":"
              << info.channelCount << ",\"defaultActive\":"
              << ((info.flags & BusInfo::kDefaultActive) ? "true" : "false")
              << '}';
  }
}

int scan(const std::string &path) {
  std::string error;
  auto module = VST3::Hosting::Module::create(path, error);
  if (!module) {
    std::cerr << "VST3 module load failed: " << error << '\n';
    return 2;
  }
  auto context = Steinberg::owned(new Steinberg::Vst::HostApplication());
  Steinberg::Vst::PluginContextFactory::instance().setPluginContext(context);
  Steinberg::Vst::PlugProvider::setErrorStream(&std::cerr);

  bool first_plugin = true;
  std::cout << '[';
  for (const auto &class_info : module->getFactory().classInfos()) {
    if (class_info.category() != kVstAudioEffectClass) continue;
    Steinberg::Vst::PlugProvider provider(module->getFactory(), class_info, true);
    if (!provider.initialize()) continue;
    IPtr<IComponent> component = provider.getComponentPtr();
    IPtr<IEditController> controller = provider.getControllerPtr();
    IPtr<IAudioProcessor> processor = Steinberg::U::cast<IAudioProcessor>(component);
    if (!component || !controller || !processor ||
        processor->canProcessSampleSize(Steinberg::Vst::kSample32) !=
            Steinberg::kResultTrue)
      continue;

    BusInfo input{};
    BusInfo output{};
    if (component->getBusInfo(Steinberg::Vst::kAudio, Steinberg::Vst::kInput,
                              0, input) != Steinberg::kResultOk ||
        component->getBusInfo(Steinberg::Vst::kAudio, Steinberg::Vst::kOutput,
                              0, output) != Steinberg::kResultOk ||
        input.busType != Steinberg::Vst::kMain ||
        output.busType != Steinberg::Vst::kMain || input.channelCount < 1 ||
        output.channelCount < 1)
      continue;

    if (!first_plugin) std::cout << ',';
    first_plugin = false;
    std::cout << "{\"kind\":\"vst3\",\"plugin\":\""
              << class_info.ID().toString() << "\",\"name\":\""
              << json_escape(class_info.name()) << "\",\"category\":\""
              << json_escape(class_info.subCategoriesString())
              << "\",\"audioIns\":" << input.channelCount
              << ",\"audioOuts\":" << output.channelCount
              << ",\"inputSymbol\":\"main-in\",\"outputSymbol\":\"main-out\""
              << ",\"params\":[";
    bool first_parameter = true;
    for (int parameter = 0; parameter < controller->getParameterCount();
         ++parameter) {
      ParameterInfo info{};
      if (controller->getParameterInfo(parameter, info) != Steinberg::kResultOk)
        continue;
      if (!first_parameter) std::cout << ',';
      print_parameter(*controller, info);
      first_parameter = false;
    }
    std::cout << "],\"requiredFeatures\":[],\"inputSymbols\":[\"main-in\"]"
              << ",\"outputSymbols\":[\"main-out\"],\"hasNativeUi\":";
    Steinberg::IPlugView *view =
        controller->createView(Steinberg::Vst::ViewType::kEditor);
    const bool has_ui = view &&
        view->isPlatformTypeSupported(Steinberg::kPlatformTypeX11EmbedWindowID) ==
            Steinberg::kResultTrue;
    std::cout << (has_ui ? "true" : "false");
    if (view) view->release();
    MemoryStream state_probe;
    const bool supports_state =
        component->getState(&state_probe) == Steinberg::kResultOk;
    std::cout << ",\"supportsState\":" << (supports_state ? "true" : "false")
              << ",\"latencySamples\":"
              << processor->getLatencySamples() << ",\"auxiliaryInputs\":[";
    print_aux_buses(*component);
    std::cout << "],\"scanStatus\":\"ready\",\"modulePath\":\""
              << json_escape(path) << "\"}";
  }
  std::cout << "]\n";
  Steinberg::Vst::PluginContextFactory::instance().setPluginContext(nullptr);
  return 0;
}

struct ParameterSlot {
  ParamID id{};
  std::atomic<double> desired{0.0};
  std::atomic<uint64_t> generation{0};
  uint64_t applied{};
};

struct AudioPort {
  Steinberg::Vst::BusDirection direction{};
  int bus{};
  int channel{};
  void *pipewire_port{};
  std::unique_ptr<float[]> fallback;
  float *buffer{};
};

class RuntimeHost;

class ComponentHandler final : public IComponentHandler {
public:
  explicit ComponentHandler(RuntimeHost &host) : host_(host) {}
  tresult PLUGIN_API beginEdit(ParamID) override { return Steinberg::kResultTrue; }
  tresult PLUGIN_API performEdit(ParamID id, ParamValue value) override;
  tresult PLUGIN_API endEdit(ParamID) override { return Steinberg::kResultTrue; }
  tresult PLUGIN_API restartComponent(Steinberg::int32 flags) override;
  tresult PLUGIN_API queryInterface(const Steinberg::TUID iid, void **object) override {
    if (Steinberg::FUnknownPrivate::iidEqual(iid, IComponentHandler::iid) ||
        Steinberg::FUnknownPrivate::iidEqual(iid, FUnknown::iid)) {
      *object = this;
      return Steinberg::kResultTrue;
    }
    *object = nullptr;
    return Steinberg::kNoInterface;
  }
  Steinberg::uint32 PLUGIN_API addRef() override { return 1000; }
  Steinberg::uint32 PLUGIN_API release() override { return 1000; }

private:
  RuntimeHost &host_;
};

class PlugFrame final : public Steinberg::IPlugFrame {
public:
  explicit PlugFrame(RuntimeHost &host) : host_(host) {}
  tresult PLUGIN_API resizeView(Steinberg::IPlugView *view,
                                Steinberg::ViewRect *size) override;
  tresult PLUGIN_API queryInterface(const Steinberg::TUID iid, void **object) override {
    if (Steinberg::FUnknownPrivate::iidEqual(iid, Steinberg::IPlugFrame::iid) ||
        Steinberg::FUnknownPrivate::iidEqual(iid, FUnknown::iid)) {
      *object = this;
      return Steinberg::kResultTrue;
    }
    *object = nullptr;
    return Steinberg::kNoInterface;
  }
  Steinberg::uint32 PLUGIN_API addRef() override { return 1000; }
  Steinberg::uint32 PLUGIN_API release() override { return 1000; }

private:
  RuntimeHost &host_;
};

class RuntimeHost {
public:
  RuntimeHost(std::string module_path, std::string class_id,
              std::string node_name, int channels, uint32_t rate)
      : module_path_(std::move(module_path)), class_id_(std::move(class_id)),
        node_name_(std::move(node_name)), channels_(channels), rate_(rate),
        handler_(*this), frame_(*this) {}

  ~RuntimeHost() { cleanup(); }

  bool initialize(char **parameters, int parameter_count) {
    std::string error;
    module_ = VST3::Hosting::Module::create(module_path_, error);
    if (!module_) return fail("VST3 module load failed: " + error);
    context_ = Steinberg::owned(new Steinberg::Vst::HostApplication());
    Steinberg::Vst::PluginContextFactory::instance().setPluginContext(context_);
    for (const auto &info : module_->getFactory().classInfos()) {
      if (info.category() == kVstAudioEffectClass &&
          info.ID().toString() == class_id_) {
        provider_ = Steinberg::owned(
            new Steinberg::Vst::PlugProvider(module_->getFactory(), info, true));
        break;
      }
    }
    if (!provider_ || !provider_->initialize())
      return fail("VST3 audio-effect class was not found or could not initialize");
    component_ = provider_->getComponentPtr();
    controller_ = provider_->getControllerPtr();
    processor_ = Steinberg::U::cast<IAudioProcessor>(component_);
    if (!component_ || !controller_ || !processor_)
      return fail("VST3 component, controller, or processor is unavailable");
    if (processor_->canProcessSampleSize(Steinberg::Vst::kSample32) !=
        Steinberg::kResultTrue)
      return fail("VST3 plug-in does not support 32-bit sample processing");
    if (controller_->getParameterCount() < 0 ||
        controller_->getParameterCount() > kMaxParameters)
      return fail("VST3 plug-in exposes too many parameters");
    controller_->setComponentHandler(&handler_);
    if (!prepare_buses()) return false;
    prepare_parameters();
    for (int index = 0; index < parameter_count; ++index)
      if (!set_parameter_argument(parameters[index])) return false;
    if (!activate_plugin()) return false;
    if (!prepare_pipewire()) return false;
    return true;
  }

  int run() {
    setvbuf(stdout, nullptr, _IOLBF, 0);
    input_.reserve(16384);
    struct pw_loop *loop = pw_main_loop_get_loop(loop_);
    pw_loop_add_io(loop, STDIN_FILENO, SPA_IO_IN | SPA_IO_HUP, false,
                   read_commands, this);
    timer_ = pw_loop_add_timer(loop, tick, this);
    struct timespec interval {0, 33333333};
    pw_loop_update_timer(loop, timer_, &interval, &interval, false);
    pw_loop_add_signal(loop, SIGTERM, stop, this);
    pw_loop_add_signal(loop, SIGINT, stop, this);
    pw_main_loop_run(loop_);
    return exit_code_;
  }

  void parameter_edit(ParamID id, ParamValue normalized) {
    if (!std::isfinite(normalized)) return;
    ParameterSlot *slot = parameter(id);
    if (!slot) return;
    slot->desired.store(std::clamp(normalized, 0.0, 1.0),
                        std::memory_order_relaxed);
    slot->generation.fetch_add(1, std::memory_order_release);
    controls_dirty_.store(true, std::memory_order_release);
  }

  void request_restart(Steinberg::int32 flags) {
    if (flags & Steinberg::Vst::kLatencyChanged)
      latency_dirty_.store(true, std::memory_order_release);
    if (flags & Steinberg::Vst::kParamValuesChanged)
      controls_dirty_.store(true, std::memory_order_release);
  }

  tresult resize_editor(Steinberg::IPlugView *view, Steinberg::ViewRect *size) {
    if (!view || view != view_.get() || !display_ || !window_ || !size)
      return Steinberg::kInvalidArgument;
    int width = std::clamp(size->getWidth(), 1, 16384);
    int height = std::clamp(size->getHeight(), 1, 16384);
    XResizeWindow(display_, window_, static_cast<unsigned>(width),
                  static_cast<unsigned>(height));
    return Steinberg::kResultTrue;
  }

private:
  bool fail(const std::string &error) {
    std::cerr << error << '\n';
    return false;
  }

  bool prepare_buses() {
    component_->setIoMode(Steinberg::Vst::kSimple);
    const int inputs = component_->getBusCount(Steinberg::Vst::kAudio,
                                               Steinberg::Vst::kInput);
    const int outputs = component_->getBusCount(Steinberg::Vst::kAudio,
                                                Steinberg::Vst::kOutput);
    if (inputs < 1 || outputs < 1 || inputs > 32 || outputs > 32)
      return fail("VST3 plug-in has an unsupported audio-bus count");
    std::vector<Steinberg::Vst::SpeakerArrangement> in_arrangements(inputs);
    std::vector<Steinberg::Vst::SpeakerArrangement> out_arrangements(outputs);
    for (int bus = 0; bus < inputs; ++bus)
      if (processor_->getBusArrangement(Steinberg::Vst::kInput, bus,
                                        in_arrangements[bus]) != Steinberg::kResultOk)
        return fail("VST3 input-bus arrangement is unavailable");
    for (int bus = 0; bus < outputs; ++bus)
      if (processor_->getBusArrangement(Steinberg::Vst::kOutput, bus,
                                        out_arrangements[bus]) != Steinberg::kResultOk)
        return fail("VST3 output-bus arrangement is unavailable");
    in_arrangements[0] = channels_ == 1 ? Steinberg::Vst::SpeakerArr::kMono
                                        : Steinberg::Vst::SpeakerArr::kStereo;
    out_arrangements[0] = in_arrangements[0];
    processor_->setBusArrangements(in_arrangements.data(), inputs,
                                   out_arrangements.data(), outputs);
    BusInfo main_input{};
    BusInfo main_output{};
    if (component_->getBusInfo(Steinberg::Vst::kAudio, Steinberg::Vst::kInput,
                               0, main_input) != Steinberg::kResultOk ||
        component_->getBusInfo(Steinberg::Vst::kAudio, Steinberg::Vst::kOutput,
                               0, main_output) != Steinberg::kResultOk ||
        main_input.channelCount != channels_ ||
        main_output.channelCount != channels_)
      return fail("VST3 plug-in rejected the requested mono/stereo layout");
    for (int bus = 0; bus < inputs; ++bus)
      component_->activateBus(Steinberg::Vst::kAudio, Steinberg::Vst::kInput,
                              bus, true);
    for (int bus = 0; bus < outputs; ++bus)
      component_->activateBus(Steinberg::Vst::kAudio, Steinberg::Vst::kOutput,
                              bus, bus == 0);
    if (!process_data_.prepare(*component_, 0, Steinberg::Vst::kSample32))
      return fail("VST3 process buffers could not be prepared");
    process_data_.processMode = Steinberg::Vst::kRealtime;
    process_data_.symbolicSampleSize = Steinberg::Vst::kSample32;
    process_data_.inputParameterChanges = &input_changes_;
    process_data_.processContext = &process_context_;
    process_context_ = {};
    process_context_.sampleRate = rate_;
    process_context_.tempo = 120.0;
    process_context_.state = Steinberg::Vst::ProcessContext::kTempoValid |
                             Steinberg::Vst::ProcessContext::kContTimeValid;
    return true;
  }

  void prepare_parameters() {
    const int count = controller_->getParameterCount();
    parameters_.reserve(count);
    input_changes_.setMaxParameters(count);
    for (int index = 0; index < count; ++index) {
      ParameterInfo info{};
      if (controller_->getParameterInfo(index, info) != Steinberg::kResultOk)
        continue;
      auto slot = std::make_unique<ParameterSlot>();
      slot->id = info.id;
      slot->desired.store(controller_->getParamNormalized(info.id),
                          std::memory_order_relaxed);
      parameters_.push_back(std::move(slot));
    }
  }

  bool set_parameter_argument(char *argument) {
    char *separator = std::strchr(argument, '=');
    if (!separator) return fail("invalid VST3 parameter argument");
    std::string_view id_text(argument, static_cast<size_t>(separator - argument));
    uint32_t id{};
    auto id_result = std::from_chars(id_text.data(), id_text.data() + id_text.size(), id);
    char *end{};
    double plain = std::strtod(separator + 1, &end);
    if (id_result.ec != std::errc{} || id_result.ptr != id_text.data() + id_text.size() ||
        !end || *end || !std::isfinite(plain))
      return fail("invalid VST3 parameter value");
    ParameterSlot *slot = parameter(id);
    if (!slot) return fail("unknown VST3 parameter ID");
    ParamValue normalized = controller_->plainParamToNormalized(id, plain);
    if (!std::isfinite(normalized)) return fail("VST3 parameter conversion failed");
    controller_->setParamNormalized(id, std::clamp(normalized, 0.0, 1.0));
    parameter_edit(id, normalized);
    return true;
  }

  bool activate_plugin() {
    Steinberg::Vst::ProcessSetup setup{Steinberg::Vst::kRealtime,
                                       Steinberg::Vst::kSample32,
                                       static_cast<Steinberg::int32>(kMaxFrames),
                                       static_cast<double>(rate_)};
    if (processor_->setupProcessing(setup) != Steinberg::kResultOk ||
        component_->setActive(true) != Steinberg::kResultOk)
      return fail("VST3 processing activation failed");
    // The SDK's own AudioHost treats setProcessing as an advisory lifecycle
    // notification because some otherwise valid processors return
    // kNotImplemented. setupProcessing and setActive remain mandatory.
    processor_->setProcessing(true);
    plugin_active_ = true;
    processing_allowed_.store(true, std::memory_order_release);
    latency_ = processor_->getLatencySamples();
    return true;
  }

  bool prepare_pipewire() {
    pw_init(nullptr, nullptr);
    loop_ = pw_main_loop_new(nullptr);
    if (!loop_) return fail("could not create PipeWire main loop");
    std::string rate = "1/" + std::to_string(rate_);
    filter_ = pw_filter_new_simple(
        pw_main_loop_get_loop(loop_), node_name_.c_str(),
        pw_properties_new(PW_KEY_NODE_NAME, node_name_.c_str(),
                          PW_KEY_NODE_DESCRIPTION, "OpenXLR VST3",
                          PW_KEY_MEDIA_TYPE, "Audio", PW_KEY_MEDIA_CATEGORY,
                          "Filter", PW_KEY_MEDIA_ROLE, "DSP", PW_KEY_NODE_RATE,
                          rate.c_str(), "node.lock-rate", "true",
                          "node.autoconnect", "false", nullptr),
        &filter_events_, this);
    if (!filter_) return fail("could not create PipeWire VST3 filter");
    if (!add_ports(Steinberg::Vst::kInput) ||
        !add_ports(Steinberg::Vst::kOutput))
      return false;
    if (pw_filter_connect(filter_, PW_FILTER_FLAG_RT_PROCESS, nullptr, 0) < 0)
      return fail("could not connect PipeWire VST3 filter");
    fcntl(STDIN_FILENO, F_SETFL, O_NONBLOCK);
    return true;
  }

  bool add_ports(Steinberg::Vst::BusDirection direction) {
    const int bus_count = component_->getBusCount(Steinberg::Vst::kAudio, direction);
    for (int bus = 0; bus < bus_count; ++bus) {
      BusInfo info{};
      if (component_->getBusInfo(Steinberg::Vst::kAudio, direction, bus, info) !=
          Steinberg::kResultOk)
        return fail("VST3 bus metadata changed during activation");
      if (direction == Steinberg::Vst::kOutput && bus != 0) continue;
      for (int channel = 0; channel < info.channelCount; ++channel) {
        AudioPort port{direction, bus, channel, nullptr,
                       std::make_unique<float[]>(kMaxFrames), nullptr};
        std::string name;
        if (direction == Steinberg::Vst::kOutput)
          name = "capture_" + std::to_string(channel);
        else if (bus == 0)
          name = "playback_" + std::to_string(channel);
        else
          name = "sidechain_" + std::to_string(bus) + "_" +
                 std::to_string(channel);
        const char *position = info.channelCount == 1 ? "MONO"
            : channel == 0 ? "FL" : channel == 1 ? "FR" : "AUX";
        port.pipewire_port = pw_filter_add_port(
            filter_, direction == Steinberg::Vst::kInput ? PW_DIRECTION_INPUT
                                                         : PW_DIRECTION_OUTPUT,
            PW_FILTER_PORT_FLAG_MAP_BUFFERS, 1,
            pw_properties_new(PW_KEY_FORMAT_DSP, "32 bit float mono audio",
                              PW_KEY_PORT_NAME, name.c_str(),
                              PW_KEY_AUDIO_CHANNEL, position, nullptr),
            nullptr, 0);
        if (!port.pipewire_port) return fail("could not create VST3 audio port");
        ports_.push_back(std::move(port));
      }
    }
    return true;
  }

  static void process_audio(void *data, struct spa_io_position *position) {
    static_cast<RuntimeHost *>(data)->process(position);
  }

  void process(struct spa_io_position *position) {
    const uint32_t frames = position->clock.duration;
    if (frames > kMaxFrames || position->clock.rate.denom != rate_) {
      audio_error_.store(true, std::memory_order_release);
      fail_open_buffers(frames);
      return;
    }
    bind_buffers(frames);
    if (!processing_allowed_.load(std::memory_order_acquire)) {
      bypass(frames);
      return;
    }
    callbacks_.fetch_add(1, std::memory_order_acq_rel);
    if (!processing_allowed_.load(std::memory_order_acquire)) {
      callbacks_.fetch_sub(1, std::memory_order_release);
      callbacks_.notify_all();
      bypass(frames);
      return;
    }
    input_changes_.clearQueue();
    for (const auto &owned_slot : parameters_) {
      ParameterSlot &slot = *owned_slot;
      uint64_t generation = slot.generation.load(std::memory_order_acquire);
      if (generation == slot.applied) continue;
      int queue_index{};
      IParamValueQueue *queue = input_changes_.addParameterData(slot.id, queue_index);
      int point_index{};
      if (queue)
        queue->addPoint(0, slot.desired.load(std::memory_order_relaxed), point_index);
      slot.applied = generation;
    }
    process_data_.numSamples = static_cast<Steinberg::int32>(frames);
    process_context_.continousTimeSamples = continuous_samples_;
    continuous_samples_ += frames;
    if (processor_->process(process_data_) != Steinberg::kResultOk) {
      // Permanently bypass this instance after a DSP error. Retrying it on
      // every quantum both floods diagnostics and risks repeated undefined
      // plug-in behavior. The daemon may later rebuild a fresh host.
      processing_allowed_.store(false, std::memory_order_release);
      processing_failed_.store(true, std::memory_order_release);
      bypass(frames);
    }
    completed_cycles_.fetch_add(1, std::memory_order_relaxed);
    callbacks_.fetch_sub(1, std::memory_order_release);
    callbacks_.notify_all();
  }

  void bind_buffers(uint32_t frames) {
    for (AudioPort &port : ports_) {
      float *buffer = static_cast<float *>(
          pw_filter_get_dsp_buffer(port.pipewire_port, frames));
      if (!buffer) {
        buffer = port.fallback.get();
        std::fill_n(buffer, frames, 0.0f);
      }
      port.buffer = buffer;
      process_data_.setChannelBuffer(port.direction, port.bus, port.channel,
                                     buffer);
    }
    for (int bus = 0; bus < process_data_.numInputs; ++bus)
      process_data_.inputs[bus].silenceFlags = 0;
    for (int bus = 0; bus < process_data_.numOutputs; ++bus)
      process_data_.outputs[bus].silenceFlags = 0;
  }

  void fail_open_buffers(uint32_t frames) {
    // A graph quantum larger than the setupProcessing maximum cannot be
    // passed to the VST3 processor or to the fixed fallback arrays. PipeWire
    // still owns full-size mapped buffers, so copy the main input directly to
    // the output without touching any bounded scratch storage.
    for (int channel = 0; channel < channels_; ++channel) {
      AudioPort *input = port(Steinberg::Vst::kInput, 0, channel);
      AudioPort *output = port(Steinberg::Vst::kOutput, 0, channel);
      if (!output) continue;
      float *out = static_cast<float *>(
          pw_filter_get_dsp_buffer(output->pipewire_port, frames));
      float *in = input ? static_cast<float *>(
          pw_filter_get_dsp_buffer(input->pipewire_port, frames)) : nullptr;
      if (!out) continue;
      if (in) std::copy_n(in, frames, out);
      else std::fill_n(out, frames, 0.0f);
    }
  }

  void bypass(uint32_t frames) {
    for (int channel = 0; channel < channels_; ++channel) {
      AudioPort *input = port(Steinberg::Vst::kInput, 0, channel);
      AudioPort *output = port(Steinberg::Vst::kOutput, 0, channel);
      if (input && output && input->buffer && output->buffer)
        std::copy_n(input->buffer, frames, output->buffer);
    }
  }

  AudioPort *port(Steinberg::Vst::BusDirection direction, int bus, int channel) {
    auto found = std::find_if(ports_.begin(), ports_.end(), [&](const AudioPort &item) {
      return item.direction == direction && item.bus == bus && item.channel == channel;
    });
    return found == ports_.end() ? nullptr : &*found;
  }

  ParameterSlot *parameter(ParamID id) {
    auto found = std::find_if(parameters_.begin(), parameters_.end(),
                              [id](const auto &slot) { return slot->id == id; });
    return found == parameters_.end() ? nullptr : found->get();
  }

  static void state_changed(void *data, enum pw_filter_state,
                            enum pw_filter_state state, const char *error) {
    RuntimeHost &host = *static_cast<RuntimeHost *>(data);
    host.streaming_ = state == PW_FILTER_STATE_STREAMING;
    if (state == PW_FILTER_STATE_ERROR) {
      std::cerr << "PipeWire: " << (error ? error : "disconnected") << '\n';
      host.exit_code_ = 1;
      pw_main_loop_quit(host.loop_);
    } else if (state == PW_FILTER_STATE_PAUSED) {
      std::cout << "latency " << host.latency_ << '\n';
      std::cout << "ready\n";
    }
  }

  static void read_commands(void *data, int fd, uint32_t) {
    RuntimeHost &host = *static_cast<RuntimeHost *>(data);
    std::array<char, 16384> buffer{};
    const ssize_t count = read(fd, buffer.data(), buffer.size());
    if (count == 0 || (count < 0 && errno != EAGAIN)) {
      pw_main_loop_quit(host.loop_);
      return;
    }
    if (count < 0) return;
    host.input_.append(buffer.data(), static_cast<size_t>(count));
    if (host.input_.size() > kMaxCommandBytes) {
      host.exit_code_ = 2;
      pw_main_loop_quit(host.loop_);
      return;
    }
    size_t newline{};
    while ((newline = host.input_.find('\n')) != std::string::npos) {
      std::string line = host.input_.substr(0, newline);
      host.input_.erase(0, newline + 1);
      host.command(line);
    }
  }

  void command(const std::string &line) {
    if (line == "show")
      std::cout << (open_ui() ? "ui opened" : "ui unavailable") << '\n';
    else if (line == "hide")
      close_ui();
    else if (line == "quit")
      pw_main_loop_quit(loop_);
    else if (line == "getstate")
      save_state();
    else if (line.rfind("loadstate ", 0) == 0)
      load_state(std::string_view(line).substr(10));
    else if (line.rfind("set ", 0) == 0)
      set_parameter_command(std::string_view(line).substr(4));
    else
      std::cerr << "unknown VST3 host command\n";
  }

  void set_parameter_command(std::string_view value) {
    size_t separator = value.find(' ');
    if (separator == std::string_view::npos) return;
    uint32_t id{};
    auto id_result = std::from_chars(value.data(), value.data() + separator, id);
    std::string plain_text(value.substr(separator + 1));
    char *end{};
    double plain = std::strtod(plain_text.c_str(), &end);
    if (id_result.ec != std::errc{} || id_result.ptr != value.data() + separator ||
        !end || *end || !std::isfinite(plain) || !parameter(id)) {
      std::cerr << "invalid VST3 control\n";
      return;
    }
    ParamValue normalized = controller_->plainParamToNormalized(id, plain);
    if (!std::isfinite(normalized)) return;
    normalized = std::clamp(normalized, 0.0, 1.0);
    controller_->setParamNormalized(id, normalized);
    parameter_edit(id, normalized);
  }

  void suspend_plugin() {
    processing_allowed_.store(false, std::memory_order_release);
    uint32_t active = callbacks_.load(std::memory_order_acquire);
    while (active != 0) {
      callbacks_.wait(active, std::memory_order_acquire);
      active = callbacks_.load(std::memory_order_acquire);
    }
    if (plugin_active_) {
      processor_->setProcessing(false);
      component_->setActive(false);
      plugin_active_ = false;
    }
  }

  bool resume_plugin() {
    if (component_->setActive(true) != Steinberg::kResultOk) {
      processing_failed_.store(true, std::memory_order_release);
      return false;
    }
    processor_->setProcessing(true);
    plugin_active_ = true;
    processing_failed_.store(false, std::memory_order_release);
    processing_allowed_.store(true, std::memory_order_release);
    return true;
  }

  void save_state() {
    suspend_plugin();
    MemoryStream component_state;
    MemoryStream controller_state;
    bool success = component_->getState(&component_state) == Steinberg::kResultOk;
    const tresult controller_result = controller_->getState(&controller_state);
    if (controller_result != Steinberg::kResultOk)
      controller_state.setSize(0);
    success = success &&
                   component_state.getSize() >= 0 && controller_state.getSize() >= 0 &&
                   static_cast<size_t>(component_state.getSize()) <= kMaxStateBytes &&
                   static_cast<size_t>(controller_state.getSize()) <= kMaxStateBytes &&
                   static_cast<size_t>(component_state.getSize() + controller_state.getSize()) +
                           parameters_.size() * 12 <= kMaxStateBytes - 16;
    if (success) {
      std::vector<uint8_t> state;
      state.reserve(static_cast<size_t>(component_state.getSize() +
                                        controller_state.getSize()) + 16 +
                    parameters_.size() * 12);
      append_u32(state, kStateMagic);
      append_u32(state, static_cast<uint32_t>(component_state.getSize()));
      append_u32(state, static_cast<uint32_t>(controller_state.getSize()));
      append_u32(state, static_cast<uint32_t>(parameters_.size()));
      if (component_state.getSize() > 0) {
        auto component_data = reinterpret_cast<const uint8_t *>(component_state.getData());
        state.insert(state.end(), component_data,
                     component_data + component_state.getSize());
      }
      if (controller_state.getSize() > 0) {
        auto controller_data = reinterpret_cast<const uint8_t *>(controller_state.getData());
        state.insert(state.end(), controller_data,
                     controller_data + controller_state.getSize());
      }
      for (const auto &slot : parameters_) {
        append_u32(state, slot->id);
        append_u64(state, std::bit_cast<uint64_t>(
                              slot->desired.load(std::memory_order_relaxed)));
      }
      std::cout << "state " << base64_encode(state.data(), state.size()) << '\n';
    } else {
      std::cout << "state-error VST3 state could not be saved\n";
    }
    resume_plugin();
  }

  void load_state(std::string_view encoded) {
    auto state = base64_decode(encoded);
    if (!state) {
      std::cout << "state-error malformed or oversized VST3 state\n";
      return;
    }
    size_t offset{};
    auto magic = read_u32(*state, offset);
    auto component_size = read_u32(*state, offset);
    auto controller_size = read_u32(*state, offset);
    auto parameter_count = read_u32(*state, offset);
    if (!magic || !component_size || !controller_size || !parameter_count ||
        *magic != kStateMagic || *parameter_count > kMaxParameters ||
        offset + *component_size + *controller_size + *parameter_count * 12 !=
            state->size()) {
      std::cout << "state-error invalid VST3 state container\n";
      return;
    }
    suspend_plugin();
    MemoryStream component_state(state->data() + offset, *component_size);
    MemoryStream controller_state(state->data() + offset + *component_size,
                                  *controller_size);
    bool success = component_->setState(&component_state) == Steinberg::kResultOk;
    component_state.seek(0, Steinberg::IBStream::kIBSeekSet, nullptr);
    controller_->setComponentState(&component_state);
    if (*controller_size > 0)
      success = controller_->setState(&controller_state) == Steinberg::kResultOk && success;
    offset += *component_size + *controller_size;
    for (uint32_t index = 0; index < *parameter_count; ++index) {
      auto id = read_u32(*state, offset);
      auto bits = read_u64(*state, offset);
      if (!id || !bits) {
        success = false;
        break;
      }
      double normalized = std::bit_cast<double>(*bits);
      if (!std::isfinite(normalized) || normalized < 0.0 || normalized > 1.0 ||
          !parameter(*id)) {
        success = false;
        continue;
      }
      controller_->setParamNormalized(*id, normalized);
      parameter_edit(*id, normalized);
    }
    success = resume_plugin() && success;
    std::cout << (success ? "state-loaded\n" : "state-error VST3 state restore failed\n");
  }

  bool open_ui() {
    if (view_) {
      XMapRaised(display_, window_);
      return true;
    }
    view_ = Steinberg::owned(
        controller_->createView(Steinberg::Vst::ViewType::kEditor));
    if (!view_ || view_->isPlatformTypeSupported(
                      Steinberg::kPlatformTypeX11EmbedWindowID) !=
                      Steinberg::kResultTrue) {
      view_ = nullptr;
      return false;
    }
    Steinberg::ViewRect size{};
    if (view_->getSize(&size) != Steinberg::kResultTrue) {
      view_ = nullptr;
      return false;
    }
    display_ = XOpenDisplay(nullptr);
    if (!display_) {
      view_ = nullptr;
      return false;
    }
    const unsigned width = static_cast<unsigned>(std::clamp(size.getWidth(), 1, 16384));
    const unsigned height = static_cast<unsigned>(std::clamp(size.getHeight(), 1, 16384));
    window_ = XCreateSimpleWindow(display_, DefaultRootWindow(display_), 0, 0,
                                  width, height, 0, 0, 0x16181d);
    XStoreName(display_, window_, "OpenXLR - Native VST3 controls");
    close_message_ = XInternAtom(display_, "WM_DELETE_WINDOW", False);
    XSetWMProtocols(display_, window_, &close_message_, 1);
    view_->setFrame(&frame_);
    if (view_->attached(reinterpret_cast<void *>(static_cast<uintptr_t>(window_)),
                        Steinberg::kPlatformTypeX11EmbedWindowID) !=
        Steinberg::kResultTrue) {
      close_ui();
      return false;
    }
    XMapRaised(display_, window_);
    XFlush(display_);
    return true;
  }

  void close_ui() {
    if (view_) {
      view_->removed();
      view_->setFrame(nullptr);
      view_ = nullptr;
    }
    if (display_) {
      if (window_) XDestroyWindow(display_, window_);
      XCloseDisplay(display_);
    }
    display_ = nullptr;
    window_ = 0;
  }

  static void tick(void *data, uint64_t) {
    static_cast<RuntimeHost *>(data)->on_tick();
  }

  void on_tick() {
    if (++heartbeat_ticks_ == 30) {
      uint64_t cycles = completed_cycles_.load(std::memory_order_relaxed);
      if (!streaming_ || cycles != last_cycles_) std::cout << "heartbeat\n";
      last_cycles_ = cycles;
      heartbeat_ticks_ = 0;
    }
    if (audio_error_.exchange(false, std::memory_order_acq_rel))
      std::cerr << "unsupported PipeWire quantum or sample-rate change\n";
    if (processing_failed_.exchange(false, std::memory_order_acq_rel))
      std::cerr << "VST3 processing failed; host is passing dry audio\n";
    // Poll once per second as well as honoring kLatencyChanged. A few real
    // plug-ins update latency without sending the restart notification.
    const bool heartbeat = heartbeat_ticks_ == 0;
    const bool latency_dirty = latency_dirty_.exchange(false, std::memory_order_acq_rel);
    if (heartbeat || latency_dirty) {
      uint32_t latency = processor_->getLatencySamples();
      if (latency != latency_) {
        latency_ = latency;
        std::cout << "latency " << latency_ << '\n';
      }
    }
    if (controls_dirty_.exchange(false, std::memory_order_acq_rel))
      for (const auto &slot : parameters_)
        std::cout << "control " << slot->id << ' '
                  << controller_->normalizedParamToPlain(
                         slot->id, slot->desired.load(std::memory_order_relaxed))
                  << '\n';
    if (display_) {
      while (XPending(display_)) {
        XEvent event{};
        XNextEvent(display_, &event);
        if (event.type == ClientMessage &&
            static_cast<Atom>(event.xclient.data.l[0]) == close_message_) {
          close_ui();
          break;
        }
      }
    }
  }

  static void stop(void *data, int) {
    pw_main_loop_quit(static_cast<RuntimeHost *>(data)->loop_);
  }

  void cleanup() {
    close_ui();
    processing_allowed_.store(false, std::memory_order_release);
    if (filter_) {
      pw_filter_disconnect(filter_);
      pw_filter_destroy(filter_);
      filter_ = nullptr;
    }
    if (loop_) {
      pw_main_loop_destroy(loop_);
      loop_ = nullptr;
    }
    if (plugin_active_ && processor_ && component_) {
      processor_->setProcessing(false);
      component_->setActive(false);
      plugin_active_ = false;
    }
    if (controller_) controller_->setComponentHandler(nullptr);
    process_data_.unprepare();
    processor_ = nullptr;
    controller_ = nullptr;
    component_ = nullptr;
    provider_ = nullptr;
    module_.reset();
    Steinberg::Vst::PluginContextFactory::instance().setPluginContext(nullptr);
    context_ = nullptr;
  }

  std::string module_path_;
  std::string class_id_;
  std::string node_name_;
  int channels_{};
  uint32_t rate_{};
  VST3::Hosting::Module::Ptr module_;
  IPtr<Steinberg::Vst::HostApplication> context_;
  IPtr<Steinberg::Vst::PlugProvider> provider_;
  IPtr<IComponent> component_;
  IPtr<IEditController> controller_;
  IPtr<IAudioProcessor> processor_;
  HostProcessData process_data_;
  Steinberg::Vst::ProcessContext process_context_{};
  ParameterChanges input_changes_;
  std::vector<std::unique_ptr<ParameterSlot>> parameters_;
  std::vector<AudioPort> ports_;
  ComponentHandler handler_;
  PlugFrame frame_;
  struct pw_main_loop *loop_{};
  struct pw_filter *filter_{};
  struct spa_source *timer_{};
  std::string input_;
  std::atomic<bool> processing_allowed_{false};
  std::atomic<bool> processing_failed_{false};
  std::atomic<bool> audio_error_{false};
  std::atomic<bool> controls_dirty_{false};
  std::atomic<bool> latency_dirty_{false};
  std::atomic<uint32_t> callbacks_{0};
  std::atomic<uint64_t> completed_cycles_{0};
  uint64_t continuous_samples_{};
  uint64_t last_cycles_{};
  uint32_t latency_{};
  unsigned heartbeat_ticks_{};
  bool streaming_{};
  bool plugin_active_{};
  int exit_code_{};
  Display *display_{};
  Window window_{};
  Atom close_message_{};
  IPtr<Steinberg::IPlugView> view_;

  inline static const struct pw_filter_events filter_events_ = {
      .version = PW_VERSION_FILTER_EVENTS,
      .destroy = nullptr,
      .state_changed = state_changed,
      .io_changed = nullptr,
      .param_changed = nullptr,
      .add_buffer = nullptr,
      .remove_buffer = nullptr,
      .process = process_audio,
      .drained = nullptr,
      .command = nullptr};
};

tresult ComponentHandler::performEdit(ParamID id, ParamValue value) {
  host_.parameter_edit(id, value);
  return Steinberg::kResultTrue;
}

tresult ComponentHandler::restartComponent(Steinberg::int32 flags) {
  host_.request_restart(flags);
  return Steinberg::kResultTrue;
}

tresult PlugFrame::resizeView(Steinberg::IPlugView *view,
                              Steinberg::ViewRect *size) {
  return host_.resize_editor(view, size);
}

int host(int argc, char **argv) {
  if (argc < 6) return 64;
  char *channels_end{};
  char *rate_end{};
  long channels = std::strtol(argv[4], &channels_end, 10);
  unsigned long rate = std::strtoul(argv[5], &rate_end, 10);
  if (!channels_end || *channels_end || !rate_end || *rate_end ||
      channels < 1 || channels > 2 || rate < 8000 || rate > 384000)
    return 64;
  const pid_t parent = getppid();
  if (prctl(PR_SET_PDEATHSIG, SIGTERM) || parent == 1 || getppid() != parent)
    return 1;
  RuntimeHost runtime(argv[1], argv[2], argv[3], static_cast<int>(channels),
                      static_cast<uint32_t>(rate));
  if (!runtime.initialize(argv + 6, argc - 6)) return 1;
  return runtime.run();
}

} // namespace

int main(int argc, char **argv) {
  if (argc == 3 && std::string_view(argv[1]) == "--scan")
    return scan(argv[2]);
  if (argc >= 6) return host(argc, argv);
  std::cerr << "usage: openxlr-vst3-host --scan MODULE.vst3\n"
               "   or: openxlr-vst3-host MODULE.vst3 CLASS_ID NODE CHANNELS RATE [ID=VALUE ...]\n";
  return 64;
}
