# Zum lokalen Testen mit MinGW (Linux): make  — die Release-DLLs baut die CI mit MSVC (build-msvc.cmd).
CFLAGS = -O2 -Wall -Wextra -shared -static-libgcc -Wl,--kill-at
LIBS = -lws2_32 -lole32

all: bin/x64/RzChromaSDK64.dll bin/x86/RzChromaSDK.dll

bin/x64/RzChromaSDK64.dll: chroma_bridge.c
	mkdir -p bin/x64
	x86_64-w64-mingw32-gcc $(CFLAGS) -o $@ $< $(LIBS)

bin/x86/RzChromaSDK.dll: chroma_bridge.c
	mkdir -p bin/x86
	i686-w64-mingw32-gcc $(CFLAGS) -o $@ $< $(LIBS)

clean:
	rm -rf bin
