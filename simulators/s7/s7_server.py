"""Siemens S7 simulator for MachineDataBrowser (python-snap7 server).

Serves data blocks DB1 (Machine), DB2 (Arrays), DB3 (DataTypes), inputs, outputs and merkers on ISO-on-TCP port 102.
tags.csv next to this file is the symbol table for these addresses. Writing true to M0.0 (PauseSimulation) freezes
every changing value; false continues. See docs/simulators.md.
"""

import datetime
import logging
import math
import os
import struct
import time

import snap7
from snap7.datatypes import S7WordLen
from snap7.type import SrvArea

PORT = int(os.environ.get("S7_SIM_PORT", "102"))
TICK_MS = int(os.environ.get("S7_SIM_TICK_MS", "50"))

logging.basicConfig(level=os.environ.get("S7_SIM_LOG", "WARNING"), format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("s7-sim")

machine = bytearray(64)
arrays = bytearray(72)
datatypes = bytearray(128)
inputs = bytearray(16)
outputs = bytearray(16)
merkers = bytearray(64)


def set_bit(buf, byte, bit, value):
    buf[byte] = buf[byte] | (1 << bit) if value else buf[byte] & ~(1 << bit)


def get_bit(buf, byte, bit):
    return bool(buf[byte] & (1 << bit))


def s7_string(buf, offset, max_len, text):
    data = text.encode("latin-1")[:max_len]
    buf[offset] = max_len
    buf[offset + 1] = len(data)
    buf[offset + 2:offset + 2 + len(data)] = data


def s7_wstring(buf, offset, max_len, text):
    data = text.encode("utf-16-be")[:max_len * 2]
    struct.pack_into(">HH", buf, offset, max_len, len(data) // 2)
    buf[offset + 4:offset + 4 + len(data)] = data


def init():
    set_bit(machine, 0, 0, True)
    s7_string(machine, 26, 20, "Line 1")
    struct.pack_into(">f", machine, 48, 1500.0)
    struct.pack_into(">h", machine, 52, 7)
    for i in range(8):
        arrays[62 + i] = i + 1

    d = datatypes
    set_bit(d, 0, 0, True)
    d[1] = 0xAB
    struct.pack_into(">H", d, 2, 0xBEEF)
    struct.pack_into(">h", d, 4, -1234)
    struct.pack_into(">I", d, 6, 0xDEADBEEF)
    struct.pack_into(">i", d, 10, -123456)
    struct.pack_into(">f", d, 14, 3.14)
    struct.pack_into(">d", d, 18, 2.718281828459045)
    d[26] = ord("S")
    s7_string(d, 28, 10, "Hello")
    s7_wstring(d, 40, 10, "Grüße")
    d[64] = 200
    struct.pack_into(">b", d, 65, -5)
    struct.pack_into(">H", d, 66, 60000)
    struct.pack_into(">I", d, 68, 4000000000)
    struct.pack_into(">q", d, 72, -9000000000)
    struct.pack_into(">Q", d, 80, 18000000000)
    struct.pack_into(">H", d, 88, (datetime.date(2026, 10, 21) - datetime.date(1990, 1, 1)).days)
    struct.pack_into(">i", d, 90, 90_000)
    struct.pack_into(">I", d, 94, ((13 * 60 + 45) * 60 + 30) * 1000 + 250)
    struct.pack_into(">Q", d, 98, 0x0123456789ABCDEF)


def tick(n, t):
    set_bit(machine, 0, 1, (int(t) // 10) % 2 == 1)
    set_bit(machine, 0, 2, n % 2 == 1)
    struct.pack_into(">f", machine, 2, 1200 + 300 * math.sin(t / 5))
    struct.pack_into(">i", machine, 6, n)
    struct.pack_into(">f", machine, 10, 60 + 5 * math.sin(t / 60))
    struct.pack_into(">d", machine, 14, 4.2 + 0.3 * math.cos(t / 7))
    struct.pack_into(">h", machine, 22, 1 + (int(t) // 5) % 4)
    struct.pack_into(">H", machine, 24, (1 << (int(t) % 16)))
    struct.pack_into(">i", machine, 54, int(t * 1000))

    for i in range(10):
        struct.pack_into(">f", arrays, i * 4, 100 * math.sin(t / 3 + i * math.pi / 5))
        struct.pack_into(">h", arrays, 40 + i * 2, (n * (i + 1)) % 32768)
    struct.pack_into(">H", arrays, 60, 1 << (n % 16))

    set_bit(inputs, 0, 0, int(t) % 2 == 1)
    inputs[1] = n % 256
    struct.pack_into(">h", inputs, 2, int(13824 + 13824 * math.sin(t / 4)))
    set_bit(outputs, 0, 0, get_bit(machine, 0, 0))
    outputs[1] = inputs[1]

    struct.pack_into(">h", merkers, 2, n % 32768)
    struct.pack_into(">f", merkers, 4, t)


class Server(snap7.server.Server):
    """python-snap7's server writes a bit write's value over the whole byte; a real CPU changes only that bit."""

    def _parse_address_specification(self, addr_spec):
        parsed = super()._parse_address_specification(addr_spec)
        if parsed and parsed.get("word_len") == S7WordLen.BIT:
            parsed["bit"] = int.from_bytes(addr_spec[9:12], "big") % 8
        return parsed

    def _parse_write_address(self, request):
        parsed = super()._parse_write_address(request)
        spec = request.get("parameters", {}).get("address_spec", {})
        if parsed and "bit" in spec:
            area, db_number, start, count, data = parsed
            buffer = self.memory_areas.get((area, db_number))
            if buffer is None or start >= len(buffer):
                return parsed
            mask = 1 << spec["bit"]
            byte = buffer[start] | mask if data[0] & 1 else buffer[start] & ~mask
            return area, db_number, start, 1, bytearray([byte])
        return parsed


def main():
    init()
    server = Server(log=False)
    server.register_area(SrvArea.DB, 1, machine)
    server.register_area(SrvArea.DB, 2, arrays)
    server.register_area(SrvArea.DB, 3, datatypes)
    server.register_area(SrvArea.PE, 0, inputs)
    server.register_area(SrvArea.PA, 0, outputs)
    server.register_area(SrvArea.MK, 0, merkers)
    server.start(tcp_port=PORT)
    print(f"S7 server ready on port {PORT} (rack 0, slot 1)", flush=True)

    n = 0
    sim_time = 0.0
    last = time.monotonic()
    try:
        while True:
            time.sleep(TICK_MS / 1000)
            now = time.monotonic()
            elapsed, last = now - last, now
            if get_bit(merkers, 0, 0):
                continue
            sim_time += elapsed
            n += 1
            tick(n, sim_time)
    except KeyboardInterrupt:
        pass
    finally:
        server.stop()


if __name__ == "__main__":
    main()
