"""Modbus TCP simulator for MachineDataBrowser: a small device with known, typed and changing values.

Pure Python, no dependencies. Answers every unit id. Layout (0-based addresses) is documented in docs/simulators.md;
values that tests assert on are static, the rest change every MODBUS_SIM_TICK_MS.
"""

import asyncio
import logging
import math
import os
import struct
import time

PORT = int(os.environ.get("MODBUS_SIM_PORT", "502"))
TICK_MS = int(os.environ.get("MODBUS_SIM_TICK_MS", "100"))
LOG = logging.getLogger("modbus-sim")

COILS = 100
DISCRETE_INPUTS = 100
INPUT_REGISTERS = 100
HOLDING_REGISTERS = 1000
PAUSE_COIL = 0


def words(fmt, value, swap=False):
    data = struct.pack(">" + fmt, value)
    regs = [int.from_bytes(data[i:i + 2], "big") for i in range(0, len(data), 2)]
    return regs[::-1] if swap else regs


def text(value, registers):
    data = value.encode("latin-1").ljust(registers * 2, b"\0")
    return [int.from_bytes(data[i:i + 2], "big") for i in range(0, len(data), 2)]


class Device:
    def __init__(self):
        self.coils = [False] * COILS
        self.inputs = [False] * DISCRETE_INPUTS
        self.input_registers = [0] * INPUT_REGISTERS
        self.holding = [0] * HOLDING_REGISTERS
        self.clock = 0.0
        self.counter = 0
        self.static()

    def put(self, area, address, values):
        area[address:address + len(values)] = values

    def static(self):
        # Coils 1-15: a fixed pattern (odd addresses on); 16-99 writable scratch.
        for a in range(1, 16):
            self.coils[a] = a % 2 == 1
        for a in range(1, 16):
            self.inputs[a] = a % 3 == 0
        h = self.holding
        h[10] = struct.unpack(">H", struct.pack(">h", -1234))[0]
        h[11] = 54321
        self.put(h, 20, words("f", 3.5))
        self.put(h, 22, words("f", 3.5, swap=True))
        self.put(h, 30, words("I", 123456789))
        self.put(h, 32, words("i", -100000))
        self.put(h, 40, words("d", 2.718281828459045))
        self.put(h, 44, words("q", -9007199254740993))
        self.put(h, 50, text("MDB-SIM", 8))
        ir = self.input_registers
        ir[10] = 4242
        self.put(ir, 20, words("f", -12.25))

    def tick(self, seconds):
        if self.coils[PAUSE_COIL]:
            return
        self.clock += seconds
        self.counter = (self.counter + 1) & 0xFFFF
        t = self.clock
        h = self.holding
        h[0] = self.counter
        h[1] = struct.unpack(">H", struct.pack(">h", int(1000 * math.sin(t))))[0]
        self.put(h, 2, words("f", 20.0 + 5.0 * math.sin(t / 5)))
        ir = self.input_registers
        ir[0] = self.counter
        self.put(ir, 2, words("f", 50.0 + 10.0 * math.cos(t / 3)))
        self.inputs[0] = int(t) % 2 == 0
        self.coils[16] = int(t / 2) % 2 == 0


EXC_FUNCTION, EXC_ADDRESS, EXC_VALUE = 1, 2, 3


def pack_bits(bits):
    out = bytearray((len(bits) + 7) // 8)
    for i, bit in enumerate(bits):
        if bit:
            out[i // 8] |= 1 << (i % 8)
    return bytes(out)


def handle(device, pdu):
    """Returns the response PDU, or (exception code) for a Modbus exception."""
    fc = pdu[0]
    if fc in (1, 2, 3, 4):
        if len(pdu) < 5:
            return EXC_VALUE
        start, count = struct.unpack(">HH", pdu[1:5])
        area, limit = {
            1: (device.coils, 2000),
            2: (device.inputs, 2000),
            3: (device.holding, 125),
            4: (device.input_registers, 125),
        }[fc]
        if not 1 <= count <= limit:
            return EXC_VALUE
        if start + count > len(area):
            return EXC_ADDRESS
        values = area[start:start + count]
        if fc in (1, 2):
            data = pack_bits(values)
        else:
            data = b"".join(struct.pack(">H", v) for v in values)
        return bytes([fc, len(data)]) + data
    if fc == 5:
        address, value = struct.unpack(">HH", pdu[1:5])
        if value not in (0, 0xFF00):
            return EXC_VALUE
        if address >= COILS:
            return EXC_ADDRESS
        device.coils[address] = value == 0xFF00
        LOG.info("coil %d = %s", address, device.coils[address])
        return pdu[:5]
    if fc == 6:
        address, value = struct.unpack(">HH", pdu[1:5])
        if address >= HOLDING_REGISTERS:
            return EXC_ADDRESS
        device.holding[address] = value
        LOG.info("hr %d = %d", address, value)
        return pdu[:5]
    if fc == 15:
        address, count, size = struct.unpack(">HHB", pdu[1:6])
        if address + count > COILS:
            return EXC_ADDRESS
        data = pdu[6:6 + size]
        for i in range(count):
            device.coils[address + i] = bool(data[i // 8] & (1 << (i % 8)))
        return pdu[:5]
    if fc == 16:
        address, count, size = struct.unpack(">HHB", pdu[1:6])
        if not 1 <= count <= 123 or size != count * 2:
            return EXC_VALUE
        if address + count > HOLDING_REGISTERS:
            return EXC_ADDRESS
        device.holding[address:address + count] = list(struct.unpack(">" + "H" * count, pdu[6:6 + size]))
        LOG.info("hr %d..%d written", address, address + count - 1)
        return pdu[:5]
    return EXC_FUNCTION


async def serve_client(device, reader, writer):
    peer = writer.get_extra_info("peername")
    LOG.info("client %s connected", peer)
    try:
        while True:
            header = await reader.readexactly(7)
            tid, proto, length, unit = struct.unpack(">HHHB", header)
            pdu = await reader.readexactly(length - 1)
            if proto != 0 or not pdu:
                break
            result = handle(device, pdu)
            if isinstance(result, int):
                result = bytes([pdu[0] | 0x80, result])
            LOG.debug("unit %d fc %d -> %d bytes", unit, pdu[0], len(result))
            writer.write(struct.pack(">HHHB", tid, 0, len(result) + 1, unit) + result)
            await writer.drain()
    except (asyncio.IncompleteReadError, ConnectionError):
        pass
    finally:
        LOG.info("client %s disconnected", peer)
        writer.close()


async def simulate(device):
    last = time.monotonic()
    while True:
        await asyncio.sleep(TICK_MS / 1000)
        now = time.monotonic()
        device.tick(now - last)
        last = now


async def main():
    logging.basicConfig(level=os.environ.get("MODBUS_SIM_LOG", "INFO"), format="%(asctime)s %(levelname)s %(message)s")
    device = Device()
    server = await asyncio.start_server(lambda r, w: serve_client(device, r, w), "0.0.0.0", PORT)
    LOG.info("Modbus TCP server ready on port %d", PORT)
    async with server:
        await asyncio.gather(server.serve_forever(), simulate(device))


if __name__ == "__main__":
    asyncio.run(main())
