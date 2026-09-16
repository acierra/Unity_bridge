SHELL = /bin/sh
CC = gcc

OUTDIR = ../../Build/Linux
SRCDIR = ../../Source

CFLAGS = -fPIC -O2 -Wall -Wextra
LDFLAGS = -shared -Wl,-rpath,'$$ORIGIN'
LIBS = -ldl

TARGET = $(OUTDIR)/libVp8DecoderBridge.so
SOURCES = $(SRCDIR)/vp8_decoder_bridge.c

all: $(TARGET)

$(TARGET): $(SOURCES)
	mkdir -p $(OUTDIR)
	$(CC) $(CFLAGS) -o $(TARGET) $(SOURCES) $(LDFLAGS) $(LIBS)

clean:
	rm -f $(TARGET)
