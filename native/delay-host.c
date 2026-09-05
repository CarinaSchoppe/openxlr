// SPDX-License-Identifier: GPL-3.0-only
// Fixed, preallocated sample delay used for route latency compensation.
#define _POSIX_C_SOURCE 200809L
#include <errno.h>
#include <fcntl.h>
#include <pipewire/filter.h>
#include <pipewire/pipewire.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <unistd.h>

enum { MAX_CHANNELS = 2, MAX_DELAY_SAMPLES = 2000000 };

typedef struct {
  struct pw_main_loop *loop;
  struct pw_filter *filter;
  struct spa_source *timer;
  void *inputs[MAX_CHANNELS];
  void *outputs[MAX_CHANNELS];
  float *delay_lines[MAX_CHANNELS];
  uint32_t channels;
  uint32_t delay;
  uint32_t position;
  int exit_code;
} DelayHost;

static void process_audio(void *data, struct spa_io_position *position) {
  DelayHost *host = data;
  const uint32_t frames = position->clock.duration;
  float *inputs[MAX_CHANNELS] = {0};
  float *outputs[MAX_CHANNELS] = {0};
  for (uint32_t channel = 0; channel < host->channels; ++channel) {
    inputs[channel] = pw_filter_get_dsp_buffer(host->inputs[channel], frames);
    outputs[channel] = pw_filter_get_dsp_buffer(host->outputs[channel], frames);
  }
  if (host->delay == 0) {
    for (uint32_t channel = 0; channel < host->channels; ++channel) {
      if (!outputs[channel])
        continue;
      if (inputs[channel])
        memcpy(outputs[channel], inputs[channel], frames * sizeof(float));
      else
        memset(outputs[channel], 0, frames * sizeof(float));
    }
    return;
  }
  for (uint32_t frame = 0; frame < frames; ++frame) {
    const uint32_t cursor = host->position;
    for (uint32_t channel = 0; channel < host->channels; ++channel) {
      if (outputs[channel])
        outputs[channel][frame] = host->delay_lines[channel][cursor];
      host->delay_lines[channel][cursor] =
          inputs[channel] ? inputs[channel][frame] : 0.0f;
    }
    if (++host->position == host->delay)
      host->position = 0;
  }
}

static void state_changed(void *data, enum pw_filter_state old,
                          enum pw_filter_state state, const char *error) {
  (void)old;
  DelayHost *host = data;
  if (state == PW_FILTER_STATE_ERROR) {
    fprintf(stderr, "PipeWire: %s\n", error ? error : "disconnected");
    host->exit_code = 1;
    pw_main_loop_quit(host->loop);
  } else if (state == PW_FILTER_STATE_PAUSED) {
    puts("ready");
    fflush(stdout);
  }
}

static const struct pw_filter_events filter_events = {
    PW_VERSION_FILTER_EVENTS, .state_changed = state_changed,
    .process = process_audio};

static void stop(void *data, int signal_number) {
  (void)signal_number;
  pw_main_loop_quit(((DelayHost *)data)->loop);
}

static void heartbeat(void *data, uint64_t expirations) {
  (void)data;
  (void)expirations;
  puts("heartbeat");
  fflush(stdout);
}

static void read_command(void *data, int fd, uint32_t mask) {
  (void)mask;
  char buffer[64];
  const ssize_t count = read(fd, buffer, sizeof(buffer) - 1);
  if (count <= 0 && (count == 0 || errno != EAGAIN)) {
    pw_main_loop_quit(((DelayHost *)data)->loop);
    return;
  }
  if (count <= 0)
    return;
  buffer[count] = 0;
  if (strstr(buffer, "quit\n"))
    pw_main_loop_quit(((DelayHost *)data)->loop);
}

static bool add_port(DelayHost *host, uint32_t channel, bool input) {
  char name[32];
  snprintf(name, sizeof(name), "%s_%u", input ? "playback" : "capture",
           channel);
  const char *position = host->channels == 1 ? "MONO" : channel == 0 ? "FL" : "FR";
  void *port = pw_filter_add_port(
      host->filter, input ? PW_DIRECTION_INPUT : PW_DIRECTION_OUTPUT,
      PW_FILTER_PORT_FLAG_MAP_BUFFERS, 1,
      pw_properties_new(PW_KEY_FORMAT_DSP, "32 bit float mono audio",
                        PW_KEY_PORT_NAME, name, PW_KEY_AUDIO_CHANNEL, position,
                        NULL),
      NULL, 0);
  if (input)
    host->inputs[channel] = port;
  else
    host->outputs[channel] = port;
  return port != NULL;
}

int main(int argc, char **argv) {
  if (argc != 5) {
    fputs("usage: openxlr-delay-host NODE CHANNELS DELAY_SAMPLES RATE\n", stderr);
    return 64;
  }
  char *channels_end = NULL, *delay_end = NULL, *rate_end = NULL;
  const unsigned long channels = strtoul(argv[2], &channels_end, 10);
  const unsigned long delay = strtoul(argv[3], &delay_end, 10);
  const unsigned long rate = strtoul(argv[4], &rate_end, 10);
  if (!channels_end || *channels_end || !delay_end || *delay_end || !rate_end ||
      *rate_end || channels < 1 || channels > MAX_CHANNELS ||
      delay > MAX_DELAY_SAMPLES || rate < 8000 || rate > 384000)
    return 64;
  const pid_t parent = getppid();
  if (prctl(PR_SET_PDEATHSIG, SIGTERM) || parent == 1 || getppid() != parent)
    return 1;

  DelayHost host = {.channels = (uint32_t)channels, .delay = (uint32_t)delay};
  for (uint32_t channel = 0; channel < host.channels; ++channel) {
    host.delay_lines[channel] = calloc(host.delay ? host.delay : 1, sizeof(float));
    if (!host.delay_lines[channel]) {
      fputs("could not allocate bounded compensation buffer\n", stderr);
      host.exit_code = 1;
      goto cleanup;
    }
  }

  pw_init(NULL, NULL);
  host.loop = pw_main_loop_new(NULL);
  if (!host.loop) {
    fputs("could not create PipeWire loop\n", stderr);
    host.exit_code = 1;
    goto cleanup_pipewire;
  }
  char rate_property[32];
  snprintf(rate_property, sizeof(rate_property), "1/%lu", rate);
  host.filter = pw_filter_new_simple(
      pw_main_loop_get_loop(host.loop), argv[1],
      pw_properties_new(PW_KEY_NODE_NAME, argv[1], PW_KEY_NODE_DESCRIPTION,
                        "OpenXLR latency compensation", PW_KEY_MEDIA_TYPE,
                        "Audio", PW_KEY_MEDIA_CATEGORY, "Filter",
                        PW_KEY_MEDIA_ROLE, "DSP", PW_KEY_NODE_RATE,
                        rate_property, "node.lock-rate", "true",
                        "node.autoconnect", "false", NULL),
      &filter_events, &host);
  if (!host.filter) {
    fputs("could not create compensation filter\n", stderr);
    host.exit_code = 1;
    goto cleanup_loop;
  }
  for (uint32_t channel = 0; channel < host.channels; ++channel)
    if (!add_port(&host, channel, true) || !add_port(&host, channel, false)) {
      fputs("could not create compensation ports\n", stderr);
      host.exit_code = 1;
      goto cleanup_filter;
    }
  if (pw_filter_connect(host.filter, PW_FILTER_FLAG_RT_PROCESS, NULL, 0) < 0) {
    fputs("could not connect compensation filter\n", stderr);
    host.exit_code = 1;
    goto cleanup_filter;
  }
  struct pw_loop *loop = pw_main_loop_get_loop(host.loop);
  fcntl(STDIN_FILENO, F_SETFL, O_NONBLOCK);
  pw_loop_add_io(loop, STDIN_FILENO, SPA_IO_IN | SPA_IO_HUP, false,
                 read_command, &host);
  host.timer = pw_loop_add_timer(loop, heartbeat, &host);
  struct timespec interval = {1, 0};
  pw_loop_update_timer(loop, host.timer, &interval, &interval, false);
  pw_loop_add_signal(loop, SIGTERM, stop, &host);
  pw_loop_add_signal(loop, SIGINT, stop, &host);
  pw_main_loop_run(host.loop);

cleanup_filter:
  pw_filter_destroy(host.filter);
cleanup_loop:
  pw_main_loop_destroy(host.loop);
cleanup_pipewire:
  pw_deinit();
cleanup:
  for (uint32_t channel = 0; channel < host.channels; ++channel)
    free(host.delay_lines[channel]);
  return host.exit_code;
}
