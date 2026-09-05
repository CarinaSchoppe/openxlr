PKGS = libpipewire-0.3 lilv-0 x11
CPPFLAGS += $(shell pkg-config --cflags $(PKGS))
CFLAGS += -std=c11 -O2 -g -Wall -Wextra -Werror -Wno-unused-parameter
LDLIBS += $(shell pkg-config --libs $(PKGS)) -ldl -lm -pthread

all: openxlr-lv2-host openxlr-vst3-host openxlr-delay-host

openxlr-lv2-host: lv2-host.c
	$(CC) $(CPPFLAGS) $(CFLAGS) $< -o $@ $(LDFLAGS) $(LDLIBS)

openxlr-delay-host: delay-host.c
	$(CC) $(CPPFLAGS) $(CFLAGS) $< -o $@ $(LDFLAGS) $(LDLIBS)

openxlr-vst3-host: vst3-host.cpp vst3/CMakeLists.txt
	cmake -S vst3 -B vst3/build -DCMAKE_BUILD_TYPE=Release
	cmake --build vst3/build --parallel
	cp vst3/build/openxlr-vst3-host $@

.PHONY: clean
clean:
	rm -f openxlr-lv2-host openxlr-vst3-host openxlr-delay-host
	rm -rf vst3/build
