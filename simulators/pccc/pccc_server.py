"""EtherNet/IP server that answers PCCC like an Allen-Bradley SLC 500, MicroLogix or PLC-5 controller.

One data table serves all three families: SLC 500 / MicroLogix requests (protected typed logical read 0xA2,
write 0xAA, masked write 0xAB) and PLC-5 requests (word range read 0x01, write 0x00, read-modify-write 0x26)
are told apart by their function code, so `plc=slc500`, `plc=micrologix` and `plc=plc5` all work against the
same container.

Implements: RegisterSession / UnregisterSession / ListIdentity / ListServices, SendRRData with the CIP Execute
PCCC service (0x4B) on the PCCC object (class 0x67), directly or inside Unconnected Send (0x52), and Get
Attributes All on the Identity object.

Environment:
  PCCC_SIM_PORT     TCP port (default 44818)
  PCCC_SIM_TICK_MS  animation tick in ms (default 100; 0 disables animations)
  PCCC_SIM_LOG      log level (default INFO; DEBUG logs every request)
  PCCC_SIM_SEED     random seed for the noisy values
"""

import asyncio
import logging
import math
import os
import random
import struct
import sys

log = logging.getLogger("pccc")

ENCAP_HEADER = struct.Struct("<HHII8sI")
CMD_LIST_SERVICES, CMD_LIST_IDENTITY, CMD_LIST_INTERFACES = 0x0004, 0x0063, 0x0064
CMD_REGISTER, CMD_UNREGISTER, CMD_SEND_RR = 0x0065, 0x0066, 0x006F

SVC_GET_ALL, SVC_EXECUTE_PCCC, SVC_UNCONNECTED_SEND = 0x01, 0x4B, 0x52
CLASS_IDENTITY, CLASS_CM, CLASS_PCCC = 0x01, 0x06, 0x67
CIP_OK, CIP_PATH_UNKNOWN, CIP_SERVICE_NOT_SUPPORTED, CIP_NOT_ENOUGH_DATA = 0x00, 0x05, 0x08, 0x13

VENDOR_ID, DEVICE_TYPE, PRODUCT_CODE, SERIAL = 1, 0x0E, 0x0060, 0x0C1A0B5F
PRODUCT_NAME = b"1747-L551 SLC 5/05 MachineDataBrowser Simulator"

# PCCC
CMD_TYPED = 0x0F
FNC_PLC5_WRITE, FNC_PLC5_READ, FNC_PLC5_RMW = 0x00, 0x01, 0x26
FNC_SLC_READ, FNC_SLC_WRITE, FNC_SLC_MASKED_WRITE = 0xA2, 0xAA, 0xAB
STS_OK, STS_ILLEGAL_COMMAND, STS_EXTENDED = 0x00, 0x10, 0xF0
EXT_ADDRESS_NOT_USABLE, EXT_FILE_WRONG_SIZE, EXT_CANNOT_DECODE = 0x06, 0x07, 0x0E
MAX_DATA = 236
# Some clients (libplctag, pycomm3) address O and I files with these codes instead of 0x82 / 0x83.
TYPE_ALIASES = {0x8B: 0x82, 0x8C: 0x83}

# file letter -> (SLC file type code, element size in bytes)
FILE_TYPES = {
    "O": (0x82, 2), "I": (0x83, 2), "S": (0x84, 2), "B": (0x85, 2), "T": (0x86, 6), "C": (0x87, 6),
    "R": (0x88, 6), "N": (0x89, 2), "F": (0x8A, 4), "ST": (0x8D, 84), "L": (0x91, 4),
}


class DataFile:
    def __init__(self, letter, number, elements):
        self.letter, self.number, self.elements = letter, number, elements
        self.type_code, self.element_size = FILE_TYPES[letter]
        self.buffer = bytearray(elements * self.element_size)

    @property
    def name(self):
        return "{}{}".format(self.letter, self.number)

    def offset(self, element, sub=0):
        return element * self.element_size + sub * 2

    def get_int(self, element, sub=0):
        return struct.unpack_from("<h", self.buffer, self.offset(element, sub))[0]

    def set_int(self, element, value, sub=0):
        value = int(value)
        value = (value + 0x8000) % 0x10000 - 0x8000
        struct.pack_into("<h", self.buffer, self.offset(element, sub), value)

    def get_bit(self, element, bit, sub=0):
        return (self.get_int(element, sub) >> bit) & 1

    def set_bit(self, element, bit, on, sub=0):
        word = self.get_int(element, sub) & 0xFFFF
        word = word | (1 << bit) if on else word & ~(1 << bit)
        self.set_int(element, word if word < 0x8000 else word - 0x10000, sub)

    def set_float(self, element, value):
        struct.pack_into("<f", self.buffer, self.offset(element), value)

    def set_long(self, element, value):
        value = (int(value) + 0x80000000) % 0x100000000 - 0x80000000
        struct.pack_into("<i", self.buffer, self.offset(element), value)

    def set_string(self, element, text):
        """ST elements: a 16-bit length, then 82 characters stored with each byte pair swapped."""
        raw = text.encode("ascii")[:82]
        padded = raw + b"\x00" * (82 - len(raw))
        swapped = bytearray(82)
        swapped[0::2], swapped[1::2] = padded[1::2], padded[0::2]
        start = self.offset(element)
        struct.pack_into("<h", self.buffer, start, len(raw))
        self.buffer[start + 2:start + 84] = swapped


class DataTable:
    def __init__(self):
        self.files = {}
        for letter, number, elements in [
            ("O", 0, 8), ("I", 1, 8), ("S", 2, 66), ("B", 3, 32), ("T", 4, 10), ("C", 5, 10), ("R", 6, 4),
            ("N", 7, 100), ("F", 8, 50), ("N", 9, 256), ("ST", 10, 10), ("L", 11, 20),
        ]:
            self.files[number] = DataFile(letter, number, elements)

    def __getitem__(self, name):
        return next(f for f in self.files.values() if f.name == name)

    def seed(self):
        n7, f8, b3, st10, l11, n9 = self["N7"], self["F8"], self["B3"], self["ST10"], self["L11"], self["N9"]
        for i in range(10, n7.elements):
            n7.set_int(i, i * 10)
        for i in range(10, f8.elements):
            f8.set_float(i, i * 1.5)
        b3.set_int(2, 0x5555)
        b3.set_int(3, -1)
        for i, text in enumerate(["Hello PCCC", "SLC 500", "MicroLogix", "PLC-5", "Recipe A", "Recipe B"]):
            st10.set_string(i, text)
        for i in range(1, l11.elements):
            l11.set_long(i, i * 100000)
        for i in range(n9.elements):
            n9.set_int(i, i)
        t4, c5, r6 = self["T4"], self["C5"], self["R6"]
        for i in range(t4.elements):
            t4.set_int(i, 100 + i * 50, sub=1)  # PRE
        t4.set_int(1, 250, sub=2)
        t4.set_bit(1, 13, True)  # T4:1.DN, a finished timer
        for i in range(c5.elements):
            c5.set_int(i, 50, sub=1)
        c5.set_int(2, 7, sub=2)
        r6.set_int(0, 10, sub=1)  # LEN
        s2 = self["S2"]
        s2.set_int(1, 0x001E)  # S:1 mode bits: remote run
        s2.set_int(57, 5)       # S:57 OS catalog number (made up but stable)

    def describe(self):
        return ["  {:<5} {:>4} elements x {:>2} bytes".format(f.name, f.elements, f.element_size)
                for f in sorted(self.files.values(), key=lambda f: f.number)]


# ---------------------------------------------------------------------------------------------------------
# PCCC
# ---------------------------------------------------------------------------------------------------------

class PcccError(Exception):
    def __init__(self, status, ext=None):
        super().__init__(status)
        self.status, self.ext = status, ext


def read_field(data, pos):
    """Addresses encode numbers up to 254 in one byte, larger ones as 0xFF followed by a 16-bit value."""
    value = data[pos]
    if value == 0xFF:
        return struct.unpack_from("<H", data, pos + 1)[0], pos + 3
    return value, pos + 1


class Pccc:
    def __init__(self, table):
        self.table = table

    def execute(self, request):
        """`request` starts at the PCCC command byte; returns the PCCC reply."""
        if len(request) < 5:
            raise ValueError("PCCC request too short")
        command, _, tns, function = request[0], request[1], struct.unpack_from("<H", request, 2)[0], request[4]
        body = request[5:]
        try:
            if command != CMD_TYPED:
                raise PcccError(STS_ILLEGAL_COMMAND)
            handler = {
                FNC_SLC_READ: self.slc_read, FNC_SLC_WRITE: self.slc_write, FNC_SLC_MASKED_WRITE: self.slc_masked_write,
                FNC_PLC5_READ: self.plc5_read, FNC_PLC5_WRITE: self.plc5_write, FNC_PLC5_RMW: self.plc5_rmw,
            }.get(function)
            if handler is None:
                log.info("Unsupported PCCC function 0x%02X", function)
                raise PcccError(STS_EXTENDED, EXT_CANNOT_DECODE)
            data = handler(body)
            return struct.pack("<BBH", command | 0x40, STS_OK, tns) + data
        except (IndexError, struct.error):
            log.info("Malformed PCCC function 0x%02X", function)
            return struct.pack("<BBH", command | 0x40, STS_EXTENDED, tns) + bytes([EXT_CANNOT_DECODE])
        except PcccError as e:
            reply = struct.pack("<BBH", command | 0x40, e.status, tns)
            return reply + (bytes([e.ext]) if e.ext is not None else b"")

    def _range(self, file_number, element, sub, size, type_code=None):
        f = self.table.files.get(file_number)
        if f is None or (type_code is not None and TYPE_ALIASES.get(type_code, type_code) != f.type_code):
            log.info("No data file %d%s", file_number, "" if type_code is None else " of type 0x%02X" % type_code)
            raise PcccError(STS_EXTENDED, EXT_ADDRESS_NOT_USABLE)
        start = f.offset(element, sub)
        if element >= f.elements or size == 0 or start + size > len(f.buffer) or size > MAX_DATA:
            log.info("%s:%d.%d: %d bytes is outside the file (%d elements)", f.name, element, sub, size, f.elements)
            raise PcccError(STS_EXTENDED, EXT_FILE_WRONG_SIZE)
        return f, start

    def _slc_address(self, body, pos):
        file_number, pos = read_field(body, pos)
        type_code, pos = read_field(body, pos)
        element, pos = read_field(body, pos)
        sub, pos = read_field(body, pos)
        return file_number, type_code, element, sub, pos

    def slc_read(self, body):
        size = body[0]
        file_number, type_code, element, sub, _ = self._slc_address(body, 1)
        f, start = self._range(file_number, element, sub, size, type_code)
        log.debug("SLC read %s:%d.%d %d bytes", f.name, element, sub, size)
        return bytes(f.buffer[start:start + size])

    def slc_write(self, body):
        size = body[0]
        file_number, type_code, element, sub, pos = self._slc_address(body, 1)
        f, start = self._range(file_number, element, sub, size, type_code)
        data = body[pos:pos + size]
        if len(data) != size:
            raise PcccError(STS_EXTENDED, EXT_FILE_WRONG_SIZE)
        f.buffer[start:start + size] = data
        log.info("SLC write %s:%d.%d (%d bytes)", f.name, element, sub, size)
        return b""

    def slc_masked_write(self, body):
        size = body[0]
        file_number, type_code, element, sub, pos = self._slc_address(body, 1)
        f, start = self._range(file_number, element, sub, size, type_code)
        mask, data = body[pos:pos + size], body[pos + size:pos + 2 * size]
        for i in range(size):
            f.buffer[start + i] = (f.buffer[start + i] & ~mask[i] & 0xFF) | (data[i] & mask[i])
        log.info("SLC masked write %s:%d.%d", f.name, element, sub)
        return b""

    @staticmethod
    def _plc5_address(body, pos):
        """Level-encoded address: a flag byte (bit n = level n present), then one field per level."""
        levels = body[pos]
        pos += 1
        fields = []
        for bit in range(8):
            if levels & (1 << bit):
                value, pos = read_field(body, pos)
                fields.append((bit, value))
        values = [v for bit, v in fields if bit >= 1]
        if len(values) < 2:
            raise PcccError(STS_EXTENDED, EXT_ADDRESS_NOT_USABLE)
        return values[0], values[1], values[2] if len(values) > 2 else 0, pos

    def plc5_read(self, body):
        offset, words = struct.unpack_from("<HH", body, 0)
        file_number, element, sub, _ = self._plc5_address(body, 4)
        f, start = self._range(file_number, element, sub, words * 2 + offset * 2)
        start += offset * 2
        log.debug("PLC-5 read %s:%d.%d %d words", f.name, element, sub, words)
        return bytes(f.buffer[start:start + words * 2])

    def plc5_write(self, body):
        offset, words = struct.unpack_from("<HH", body, 0)
        file_number, element, sub, pos = self._plc5_address(body, 4)
        f, start = self._range(file_number, element, sub, words * 2 + offset * 2)
        start += offset * 2
        data = body[pos:pos + words * 2]
        if len(data) != words * 2:
            raise PcccError(STS_EXTENDED, EXT_FILE_WRONG_SIZE)
        f.buffer[start:start + len(data)] = data
        log.info("PLC-5 write %s:%d.%d (%d words)", f.name, element, sub, words)
        return b""

    def plc5_rmw(self, body):
        file_number, element, sub, pos = self._plc5_address(body, 0)
        f, start = self._range(file_number, element, sub, 2)
        and_mask, or_mask = body[pos:pos + 2], body[pos + 2:pos + 4]
        for i in range(2):
            f.buffer[start + i] = (f.buffer[start + i] & and_mask[i]) | or_mask[i]
        log.info("PLC-5 read-modify-write %s:%d.%d", f.name, element, sub)
        return b""


# ---------------------------------------------------------------------------------------------------------
# CIP and EtherNet/IP encapsulation
# ---------------------------------------------------------------------------------------------------------

def cip_reply(service, status=CIP_OK, data=b""):
    return struct.pack("<BBBB", service | 0x80, 0, status, 0) + data


def path_classes(request):
    """Class ids in a CIP request path (8- and 16-bit logical segments)."""
    words = request[1]
    pos, end, classes = 2, 2 + words * 2, []
    while pos < end:
        seg = request[pos]
        size = seg & 0x03
        if seg & 0xFC == 0x20:
            classes.append(request[pos + 1] if size == 0 else struct.unpack_from("<H", request, pos + 2)[0])
        pos += 2 if size == 0 else 4 if size == 1 else 6
    return classes


def identity_attributes():
    return struct.pack("<HHHBBHI", VENDOR_ID, DEVICE_TYPE, PRODUCT_CODE, 5, 5, 0x3060, SERIAL) + bytes([len(PRODUCT_NAME)]) + PRODUCT_NAME


class Session:
    _next_session = 0x2000

    def __init__(self, pccc, reader, writer):
        self.pccc = pccc
        self.reader = reader
        self.writer = writer
        self.handle = 0

    async def run(self):
        peer = self.writer.get_extra_info("peername")
        log.info("Client connected %s", peer)
        try:
            while True:
                header = await self.reader.readexactly(ENCAP_HEADER.size)
                command, length, session, _, context, _ = ENCAP_HEADER.unpack(header)
                payload = await self.reader.readexactly(length) if length else b""
                answer = self.dispatch(command, session, context, payload)
                if answer is None:
                    if command == CMD_UNREGISTER:
                        break
                    continue
                self.writer.write(answer)
                await self.writer.drain()
        except (asyncio.IncompleteReadError, ConnectionError):
            pass
        except Exception:
            log.exception("Session failed")
        finally:
            log.info("Client disconnected %s", peer)
            self.writer.close()

    def frame(self, command, context, payload, status=0):
        return ENCAP_HEADER.pack(command, len(payload), self.handle, status, context, 0) + payload

    def dispatch(self, command, session, context, payload):
        if command == CMD_REGISTER:
            Session._next_session += 1
            self.handle = Session._next_session
            return self.frame(command, context, struct.pack("<HH", 1, 0))
        if command == CMD_UNREGISTER:
            return None
        if command == CMD_LIST_IDENTITY:
            socket = struct.pack(">HHI8x", 2, 44818, 0)
            body = struct.pack("<H", 1) + socket + identity_attributes() + b"\x03"
            return self.frame(command, context, struct.pack("<HHH", 1, 0x0C, len(body)) + body)
        if command == CMD_LIST_SERVICES:
            item = struct.pack("<HHHH", 0x0100, 20, 1, 0x0120) + b"Communications\x00\x00"
            return self.frame(command, context, struct.pack("<H", 1) + item)
        if command == CMD_LIST_INTERFACES:
            return self.frame(command, context, struct.pack("<H", 0))
        if session != self.handle or not self.handle:
            return self.frame(command, context, b"", status=0x0064)
        if command == CMD_SEND_RR:
            return self.send_rr(context, payload)
        log.info("Unsupported encapsulation command 0x%04X", command)
        return self.frame(command, context, b"", status=0x0001)

    def send_rr(self, context, payload):
        count = struct.unpack_from("<H", payload, 6)[0]
        pos, request = 8, b""
        for _ in range(count):
            type_id, length = struct.unpack_from("<HH", payload, pos)
            if type_id == 0x00B2:
                request = payload[pos + 4:pos + 4 + length]
            pos += 4 + length
        answer = self.cip(request)
        out = struct.pack("<IHH", 0, 0, 2) + struct.pack("<HH", 0, 0) + struct.pack("<HH", 0x00B2, len(answer)) + answer
        return self.frame(CMD_SEND_RR, context, out)

    def cip(self, request):
        if len(request) < 2:
            return cip_reply(request[0] if request else 0, CIP_NOT_ENOUGH_DATA)
        service = request[0]
        classes = path_classes(request)
        body = request[2 + request[1] * 2:]
        if service == SVC_UNCONNECTED_SEND and classes == [CLASS_CM]:
            size = struct.unpack_from("<H", body, 2)[0]
            return self.cip(body[4:4 + size])
        if service == SVC_GET_ALL and classes == [CLASS_IDENTITY]:
            return cip_reply(service, CIP_OK, identity_attributes())
        if service == SVC_EXECUTE_PCCC and classes == [CLASS_PCCC]:
            requester = body[:body[0]]
            log.debug("PCCC %s", body[len(requester):].hex(" "))
            try:
                return cip_reply(service, CIP_OK, requester + self.pccc.execute(body[len(requester):]))
            except ValueError:
                return cip_reply(service, CIP_NOT_ENOUGH_DATA)
        log.info("Unsupported CIP request svc=0x%02X classes=%s", service, classes)
        return cip_reply(service, CIP_SERVICE_NOT_SUPPORTED if classes else CIP_PATH_UNKNOWN)


# ---------------------------------------------------------------------------------------------------------
# Animations and main
# ---------------------------------------------------------------------------------------------------------

def step(table, tick, tick_ms):
    """One animation step; `tick` counts ticks of the (pausable) simulation clock."""
    seconds = tick * tick_ms / 1000.0
    per_second = max(1, round(1000 / tick_ms))
    n7, f8, i1, o0, b3, s2, l11 = table["N7"], table["F8"], table["I1"], table["O0"], table["B3"], table["S2"], table["L11"]

    n7.set_int(0, (tick // per_second) % 32768)                        # heartbeat, 1 s
    n7.set_int(1, round(1000 * math.sin(2 * math.pi * seconds / 10)))  # sine, 10 s period
    n7.set_int(2, tick % 1001)                                           # ramp per tick
    n7.set_int(3, max(-1000, min(1000, n7.get_int(3) + random.randint(-5, 5))))
    f8.set_float(0, 50 + 20 * math.sin(2 * math.pi * seconds / 30))      # temperature
    f8.set_float(1, (seconds * 10) % 100)                                # ramp 0..100 over 10 s
    f8.set_float(2, 4.2 + random.gauss(0, 0.05))                          # pressure
    l11.set_long(0, int(seconds * 1000))                                 # ms of simulated time
    i1.set_int(0, 1 << ((tick // per_second) % 16))                      # walking input bit
    o0.set_int(0, i1.get_int(0))
    b3.set_int(1, tick // per_second)                                    # binary seconds count
    s2.set_int(4, tick * tick_ms // 10)                                  # S:4 free-running clock, 10 ms

    # T4:0 a 0.01 s timer timing to its preset and restarting; EN 15, TT 14, DN 13.
    t4 = table["T4"]
    pre = t4.get_int(0, sub=1)
    acc = t4.get_int(0, sub=2) + max(1, tick_ms // 10)
    if acc > pre + 50:
        acc = 0
    t4.set_int(0, min(acc, pre), sub=2)
    t4.set_bit(0, 15, True)
    t4.set_bit(0, 14, acc < pre)
    t4.set_bit(0, 13, acc >= pre)

    # C5:0 counts up once a second; CU 15, DN 13, wraps at twice its preset.
    if tick % per_second == 0:
        c5 = table["C5"]
        count = (c5.get_int(0, sub=2) + 1) % (2 * c5.get_int(0, sub=1))
        c5.set_int(0, count, sub=2)
        c5.set_bit(0, 15, True)
        c5.set_bit(0, 13, count >= c5.get_int(0, sub=1))

        # R6:0 a shift register position walking 0..LEN; EN 15, DN 13.
        r6 = table["R6"]
        length = max(1, r6.get_int(0, sub=1))
        pos = (r6.get_int(0, sub=2) + 1) % (length + 1)
        r6.set_int(0, pos, sub=2)
        r6.set_bit(0, 15, True)
        r6.set_bit(0, 13, pos == length)


async def animate(table, tick_ms):
    print("Animating every {} ms; pause with B3:0/0".format(tick_ms), flush=True)
    tick = 0
    paused = False
    b3 = table["B3"]
    while True:
        # B3:0/0 freezes every value; the simulation clock stops too, so values resume where they were.
        if b3.get_bit(0, 0):
            if not paused:
                paused = True
                log.info("Simulation paused")
        else:
            if paused:
                paused = False
                log.info("Simulation resumed")
            try:
                step(table, tick, tick_ms)
            except Exception:
                log.exception("Animation failed")
            tick += 1
        await asyncio.sleep(tick_ms / 1000.0)


async def serve():
    logging.basicConfig(level=os.environ.get("PCCC_SIM_LOG", "INFO").upper(), stream=sys.stderr,
                        format="%(asctime)s %(levelname)s %(message)s")
    port = int(os.environ.get("PCCC_SIM_PORT", "44818"))
    tick_ms = int(os.environ.get("PCCC_SIM_TICK_MS", "100"))
    table = DataTable()
    table.seed()
    print("PCCC simulator (SLC 500 / MicroLogix / PLC-5): {} data files".format(len(table.files)), flush=True)
    for line in table.describe():
        print(line, flush=True)
    server = await asyncio.start_server(lambda r, w: Session(Pccc(table), r, w).run(), "0.0.0.0", port)
    if tick_ms > 0:
        step(table, 0, tick_ms)
        asyncio.get_running_loop().create_task(animate(table, tick_ms))
    print("PCCC server ready on port {}".format(port), flush=True)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    random.seed(os.environ.get("PCCC_SIM_SEED"))
    asyncio.run(serve())
