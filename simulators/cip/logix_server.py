"""EtherNet/IP + CIP server that answers like a ControlLogix controller, backed by `controller.Controller`.

Implements: RegisterSession / UnregisterSession / ListIdentity, SendRRData (unconnected, including Unconnected
Send 0x52 and Forward Open / Large Forward Open / Forward Close), SendUnitData (connected), Multiple Service
Packet, Read Tag (0x4C), Read Tag Fragmented (0x52), Write Tag (0x4D), Write Tag Fragmented (0x53),
Read-Modify-Write (0x4E), Get Instance Attribute List (0x55, `@tags`), template Get Attribute List (0x03) and
Read Template (0x4C, `@udt/<id>`), Get Attributes All on the Identity object.

Environment:
  CIP_SIM_PORT          TCP port (default 44818)
  CIP_SIM_FAST_TICK_MS  animation base tick in ms (default 10; 0 disables animations)
  CIP_SIM_BULK_TAGS     number of Bulk_NNNN controller tags (default 3000)
  CIP_SIM_PROGRAMS      number of generated Program:LineNN scopes (default 20)
  CIP_SIM_LOG           log level (default INFO; DEBUG logs every request)
"""

import asyncio
import logging
import math
import os
import random
import struct
import sys
import time

from controller import Controller, animations, seed
from logix_model import Atomic, Udt

log = logging.getLogger("cip")

ENCAP_HEADER = struct.Struct("<HHII8sI")
CMD_LIST_SERVICES, CMD_LIST_IDENTITY, CMD_LIST_INTERFACES = 0x0004, 0x0063, 0x0064
CMD_REGISTER, CMD_UNREGISTER, CMD_SEND_RR, CMD_SEND_UNIT = 0x0065, 0x0066, 0x006F, 0x0070

SVC_GET_ALL, SVC_GET_LIST, SVC_MULTIPLE = 0x01, 0x03, 0x0A
SVC_READ, SVC_WRITE, SVC_RMW, SVC_READ_FRAG, SVC_WRITE_FRAG = 0x4C, 0x4D, 0x4E, 0x52, 0x53
SVC_FORWARD_CLOSE, SVC_UNCONNECTED_SEND, SVC_FORWARD_OPEN, SVC_LARGE_FORWARD_OPEN = 0x4E, 0x52, 0x54, 0x5B
SVC_LIST_TAGS = 0x55

CLASS_IDENTITY, CLASS_ROUTER, CLASS_CM, CLASS_PROGRAM_NAME = 0x01, 0x02, 0x06, 0x64
CLASS_SYMBOL, CLASS_TEMPLATE = 0x6B, 0x6C

OK, CONNECTION_FAILURE, PATH_SEGMENT_ERROR, PATH_UNKNOWN, PARTIAL = 0x00, 0x01, 0x04, 0x05, 0x06
SERVICE_NOT_SUPPORTED, ATTRIBUTE_NOT_SUPPORTED, NOT_ENOUGH_DATA, TOO_MUCH_DATA = 0x08, 0x14, 0x13, 0x15
EMBEDDED_ERROR, GENERAL_ERROR = 0x1E, 0xFF
EXT_OUT_OF_RANGE, EXT_TYPE_MISMATCH = 0x2105, 0x2107

VENDOR_ID, DEVICE_TYPE, PRODUCT_CODE, SERIAL = 1, 0x0E, 0x0037, 0x0C1A0B5E
PRODUCT_NAME = b"1756-L83E/B MachineDataBrowser Simulator"
CONTROLLER_NAME = b"MachineDataBrowserSim"
UNCONNECTED_LIMIT = 500


def reply(service, status=OK, data=b"", ext=None):
    if ext is None:
        return struct.pack("<BBBB", service | 0x80, 0, status, 0) + data
    return struct.pack("<BBBBH", service | 0x80, 0, status, 1, ext) + data


def parse_path(data, pos, words):
    end = pos + words * 2
    segments = []
    while pos < end:
        seg = data[pos]
        if seg == 0x91:
            length = data[pos + 1]
            segments.append(("symbol", data[pos + 2:pos + 2 + length].decode("ascii", "replace")))
            pos += 2 + length + (length & 1)
            continue
        kind = {0x20: "class", 0x24: "instance", 0x28: "element", 0x30: "attribute", 0x2C: "point"}.get(seg & 0xFC)
        if kind is None:
            raise ValueError("unsupported path segment 0x{:02X}".format(seg))
        size = seg & 0x03
        if size == 0:
            segments.append((kind, data[pos + 1])); pos += 2
        elif size == 1:
            segments.append((kind, struct.unpack_from("<H", data, pos + 2)[0])); pos += 4
        elif size == 2:
            segments.append((kind, struct.unpack_from("<I", data, pos + 2)[0])); pos += 6
        else:
            raise ValueError("reserved segment size")
    return segments


def identity_attributes():
    return struct.pack("<HHHBBHI", VENDOR_ID, DEVICE_TYPE, PRODUCT_CODE, 33, 11, 0x3060, SERIAL) + bytes([len(PRODUCT_NAME)]) + PRODUCT_NAME


class Cip:
    """Stateless CIP request dispatcher; `limit` is the reply payload budget of the calling connection."""

    def __init__(self, ctl):
        self.ctl = ctl

    def handle(self, request, limit):
        if len(request) < 2:
            return reply(request[0] if request else 0, NOT_ENOUGH_DATA)
        service, words = request[0], request[1]
        try:
            segments = parse_path(request, 2, words)
        except (ValueError, IndexError, struct.error):
            return reply(service, PATH_SEGMENT_ERROR)
        body = request[2 + words * 2:]
        classes = [v for k, v in segments if k == "class"]
        instances = [v for k, v in segments if k == "instance"]
        symbols = [v for k, v in segments if k == "symbol"]
        log.debug("CIP svc=0x%02X path=%s body=%d", service, segments, len(body))
        try:
            if service == SVC_MULTIPLE and classes == [CLASS_ROUTER]:
                return self._multiple(body, limit)
            if classes == [CLASS_SYMBOL] and service == SVC_LIST_TAGS:
                return self._list_tags(service, symbols, instances[0] if instances else 0, body, limit)
            if classes == [CLASS_TEMPLATE] and not symbols:
                return self._template(service, instances[0] if instances else 0, body, limit)
            if classes == [CLASS_IDENTITY] and service == SVC_GET_ALL:
                return reply(service, OK, identity_attributes())
            if classes == [CLASS_PROGRAM_NAME] and service == SVC_GET_ALL:
                return reply(service, OK, struct.pack("<H", len(CONTROLLER_NAME)) + CONTROLLER_NAME)
            if symbols and not classes:
                return self._tag_service(service, segments, body, limit)
            if classes == [CLASS_SYMBOL] and instances and service in (SVC_READ, SVC_READ_FRAG, SVC_WRITE, SVC_WRITE_FRAG, SVC_RMW):
                # Symbol-instance addressing (class 0x6B / instance id) instead of the tag name.
                resolved = self._by_instance(segments)
                if resolved is None:
                    return reply(service, PATH_SEGMENT_ERROR)
                return self._tag_service(service, resolved, body, limit)
        except (struct.error, IndexError):
            return reply(service, NOT_ENOUGH_DATA)
        log.info("Unsupported CIP request svc=0x%02X path=%s", service, segments)
        return reply(service, SERVICE_NOT_SUPPORTED)

    # -- Multiple Service Packet -----------------------------------------------------------------------------

    def _multiple(self, body, limit):
        count = struct.unpack_from("<H", body, 0)[0]
        offsets = list(struct.unpack_from("<{}H".format(count), body, 2)) + [len(body)]
        header = 2 + 2 * count
        budget = max(limit - 4 - header, 64)
        replies = []
        for i in range(count):
            sub = body[offsets[i]:offsets[i + 1]]
            answer = self.handle(sub, max(budget // max(count - i, 1), 64))
            budget -= len(answer)
            replies.append(answer)
        status = OK if all(r[2] in (OK, PARTIAL) for r in replies) else EMBEDDED_ERROR
        position, table = header, []
        for r in replies:
            table.append(position)
            position += len(r)
        data = struct.pack("<H", count) + struct.pack("<{}H".format(count), *table) + b"".join(replies)
        return reply(SVC_MULTIPLE, status, data)

    # -- @tags -----------------------------------------------------------------------------------------------

    def _list_tags(self, service, symbols, start, body, limit):
        if len(symbols) > 1:
            return reply(service, PATH_UNKNOWN)
        scope = self.ctl.scope(symbols[0] if symbols else "")
        if scope is None:
            return reply(service, PATH_UNKNOWN)
        count = struct.unpack_from("<H", body, 0)[0] if len(body) >= 2 else 0
        attributes = list(struct.unpack_from("<{}H".format(count), body, 2)) if count else [1, 2]
        log.info("@tags scope=%s start=%d attributes=%s", scope.name or "controller", start, attributes)
        page, size, status = [], 0, OK
        for tag in scope.listing():
            if tag.instance_id < start:
                continue
            entry = struct.pack("<I", tag.instance_id) + b"".join(self._attribute(tag, a) for a in attributes)
            if size + len(entry) > limit:
                status = PARTIAL
                break
            page.append(entry)
            size += len(entry)
        return reply(service, status, b"".join(page))

    @staticmethod
    def _attribute(tag, attribute):
        if attribute == 1:
            name = tag.name.encode("ascii")
            return struct.pack("<H", len(name)) + name
        if attribute == 2:
            return struct.pack("<H", tag.symbol_type)
        if attribute == 3:
            return struct.pack("<I", 0x1000 + tag.instance_id * 0x100)
        if attribute in (5, 6):
            return struct.pack("<I", 0)
        if attribute == 7:
            return struct.pack("<H", tag.element_size)
        if attribute == 8:
            dims = (tag.dims + [0, 0, 0])[:3]
            return struct.pack("<III", *dims)
        if attribute == 10:
            return b"\x00"
        return b""

    # -- @udt ------------------------------------------------------------------------------------------------

    def _template(self, service, template_id, body, limit):
        udt = self.ctl.templates.by_id.get(template_id)
        if udt is None:
            return reply(service, PATH_UNKNOWN)
        if service == SVC_GET_LIST:
            count = struct.unpack_from("<H", body, 0)[0]
            data = struct.pack("<H", count)
            for attribute in struct.unpack_from("<{}H".format(count), body, 2):
                value = {1: struct.pack("<H", udt.handle), 2: struct.pack("<H", len(udt.members)),
                         4: struct.pack("<I", udt.definition_words), 5: struct.pack("<I", udt.size)}.get(attribute)
                data += struct.pack("<HH", attribute, OK if value else ATTRIBUTE_NOT_SUPPORTED) + (value or b"")
            log.info("@udt/0x%03X %s attributes", udt.id, udt.name)
            return reply(service, OK, data)
        if service == SVC_READ:
            offset, requested = struct.unpack_from("<IH", body, 0)
            chunk_limit = requested if requested <= limit else limit // 4 * 4
            chunk = udt.definition[offset:offset + chunk_limit]
            more = offset + len(chunk) < len(udt.definition)
            return reply(service, PARTIAL if more else OK, chunk)
        return reply(service, SERVICE_NOT_SUPPORTED)

    # -- tag services ----------------------------------------------------------------------------------------

    def _by_instance(self, segments):
        scope_name = segments[0][1] if segments[0][0] == "symbol" else ""
        scope = self.ctl.scope(scope_name)
        instance = next(v for k, v in segments if k == "instance")
        tag = scope.by_instance.get(instance) if scope else None
        if tag is None:
            return None
        rest = [s for s in segments if s[0] not in ("class", "instance")]
        rest = rest[1:] if scope_name else rest
        return ([("symbol", scope.name)] if scope_name else []) + [("symbol", tag.name)] + rest

    def _tag_service(self, service, segments, body, limit):
        ref = self.ctl.resolve(segments)
        if ref is None:
            log.info("Unknown tag path %s", _format(segments))
            return reply(service, PATH_SEGMENT_ERROR)  # Logix: "IOI could not be deciphered or tag does not exist"
        if service in (SVC_READ, SVC_READ_FRAG):
            return self._read(service, ref, body, limit)
        if service in (SVC_WRITE, SVC_WRITE_FRAG):
            return self._write(service, ref, body)
        if service == SVC_RMW:
            return self._rmw(service, ref, body)
        return reply(service, SERVICE_NOT_SUPPORTED)

    def _read(self, service, ref, body, limit):
        elements = struct.unpack_from("<H", body, 0)[0] if len(body) >= 2 else 1
        offset = struct.unpack_from("<I", body, 2)[0] if service == SVC_READ_FRAG and len(body) >= 6 else 0
        if elements == 0 or elements > ref.available:
            if not ref.bool_array or math.ceil(elements / 32) > ref.available:
                return reply(service, GENERAL_ERROR, ext=EXT_OUT_OF_RANGE)
            elements = math.ceil(elements / 32)  # BOOL[] counts may be given in bits
        if ref.bit is not None:
            data = bytes([(ref.tag.buffer[ref.offset] >> ref.bit) & 1])
        else:
            data = bytes(ref.tag.buffer[ref.offset:ref.offset + elements * ref.element_size])
        type_bytes = ref.type_bytes
        budget = max((limit - len(type_bytes) - 4) // 8 * 8, 8)
        chunk = data[offset:offset + budget]
        status = PARTIAL if offset + len(chunk) < len(data) else OK
        return reply(service, status, type_bytes + chunk)

    def _write(self, service, ref, body):
        type_code = struct.unpack_from("<H", body, 0)[0]
        pos = 2
        if type_code == 0x02A0:
            handle = struct.unpack_from("<H", body, 2)[0]
            pos = 4
            if not isinstance(ref.type, Udt) or handle != ref.type.handle:
                return reply(service, GENERAL_ERROR, ext=EXT_TYPE_MISMATCH)
        elif ref.bool_array:
            if type_code not in (0xD3, 0xC1):
                return reply(service, GENERAL_ERROR, ext=EXT_TYPE_MISMATCH)
        elif not isinstance(ref.type, Atomic) or type_code != ref.type.code:
            return reply(service, GENERAL_ERROR, ext=EXT_TYPE_MISMATCH)
        elements = struct.unpack_from("<H", body, pos)[0]
        pos += 2
        offset = 0
        if service == SVC_WRITE_FRAG:
            offset = struct.unpack_from("<I", body, pos)[0]
            pos += 4
        data = body[pos:]
        if ref.bit is not None:
            if data[:1] and data[0]:
                ref.tag.buffer[ref.offset] |= 1 << ref.bit
            else:
                ref.tag.buffer[ref.offset] &= ~(1 << ref.bit) & 0xFF
            return reply(service)
        total = min(elements, ref.available) * ref.element_size
        data = data[:max(total - offset, 0)]
        if elements > ref.available or offset + len(data) > total:
            return reply(service, GENERAL_ERROR, ext=EXT_OUT_OF_RANGE)
        start = ref.offset + offset
        ref.tag.buffer[start:start + len(data)] = data
        log.info("Write %s (%d bytes at +%d)", ref.tag.name, len(data), offset)
        return reply(service)

    def _rmw(self, service, ref, body):
        size = struct.unpack_from("<H", body, 0)[0]
        or_mask, and_mask = body[2:2 + size], body[2 + size:2 + 2 * size]
        buffer = ref.tag.buffer
        for i in range(size):
            buffer[ref.offset + i] = (buffer[ref.offset + i] | or_mask[i]) & and_mask[i]
        return reply(service)


def _format(segments):
    text = ""
    for kind, value in segments:
        text += "[{}]".format(value) if kind == "element" else ("." if text else "") + str(value)
    return text


# ---------------------------------------------------------------------------------------------------------
# EtherNet/IP encapsulation
# ---------------------------------------------------------------------------------------------------------

class Session:
    _next_session = 0x1000
    _next_connection = 0x10000

    def __init__(self, cip, reader, writer):
        self.cip = cip
        self.reader = reader
        self.writer = writer
        self.handle = 0
        self.connections = {}  # O->T id we assigned -> (T->O id, max payload)

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
            return self.frame(command, context, self.identity_item())
        if command == CMD_LIST_SERVICES:
            item = struct.pack("<HHHH", 0x0100, 20, 1, 0x0120) + b"Communications\x00\x00"
            return self.frame(command, context, struct.pack("<H", 1) + item)
        if command == CMD_LIST_INTERFACES:
            return self.frame(command, context, struct.pack("<H", 0))
        if session != self.handle or not self.handle:
            return self.frame(command, context, b"", status=0x0064)
        if command == CMD_SEND_RR:
            return self.send_rr(context, payload)
        if command == CMD_SEND_UNIT:
            return self.send_unit(context, payload)
        log.info("Unsupported encapsulation command 0x%04X", command)
        return self.frame(command, context, b"", status=0x0001)

    def identity_item(self):
        socket = struct.pack(">HHI8x", 2, 44818, 0)
        body = struct.pack("<H", 1) + socket + identity_attributes() + b"\x03"
        return struct.pack("<HHH", 1, 0x0C, len(body)) + body

    @staticmethod
    def items(payload):
        count = struct.unpack_from("<H", payload, 6)[0]
        pos, items = 8, []
        for _ in range(count):
            type_id, length = struct.unpack_from("<HH", payload, pos)
            items.append((type_id, payload[pos + 4:pos + 4 + length]))
            pos += 4 + length
        return items

    def send_rr(self, context, payload):
        items = self.items(payload)
        request = next(data for type_id, data in items if type_id == 0x00B2)
        answer = self.unconnected(request)
        out = struct.pack("<IHH", 0, 0, 2) + struct.pack("<HH", 0, 0) + struct.pack("<HH", 0x00B2, len(answer)) + answer
        return self.frame(CMD_SEND_RR, context, out)

    def unconnected(self, request):
        service = request[0]
        segments = parse_path(request, 2, request[1])
        body = request[2 + request[1] * 2:]
        if [v for k, v in segments if k == "class"] == [CLASS_CM]:
            if service == SVC_UNCONNECTED_SEND:
                size = struct.unpack_from("<H", body, 2)[0]
                return self.unconnected(body[4:4 + size])
            if service in (SVC_FORWARD_OPEN, SVC_LARGE_FORWARD_OPEN):
                return self.forward_open(service, body)
            if service == SVC_FORWARD_CLOSE:
                serial, vendor, originator = struct.unpack_from("<HHI", body, 2)
                return reply(service, OK, struct.pack("<HHIBB", serial, vendor, originator, 0, 0))
        return self.cip.handle(request, UNCONNECTED_LIMIT)

    def forward_open(self, service, body):
        _, t2o_id, serial, vendor, originator = struct.unpack_from("<IIHHI", body, 2)
        large = service == SVC_LARGE_FORWARD_OPEN
        o2t_params = struct.unpack_from("<I" if large else "<H", body, 26)[0]
        size = o2t_params & (0xFFFF if large else 0x01FF)
        Session._next_connection += 1
        o2t_id = Session._next_connection
        self.connections[o2t_id] = (t2o_id, max(size - 16, 64))
        log.info("Forward Open (%s) size=%d", "large" if large else "small", size)
        rpi = struct.unpack_from("<I", body, 22)[0]
        data = struct.pack("<IIHHIIIBB", o2t_id, t2o_id, serial, vendor, originator, rpi, rpi, 0, 0)
        return reply(service, OK, data)

    def send_unit(self, context, payload):
        items = self.items(payload)
        address = next(data for type_id, data in items if type_id == 0x00A1)
        data = next(data for type_id, data in items if type_id == 0x00B1)
        o2t_id = struct.unpack_from("<I", address, 0)[0]
        connection = self.connections.get(o2t_id)
        if connection is None:
            return self.frame(CMD_SEND_UNIT, context, b"", status=0x0064)
        t2o_id, limit = connection
        sequence = data[:2]
        answer = sequence + self.cip.handle(data[2:], limit)
        out = struct.pack("<IHH", 0, 0, 2) + struct.pack("<HHI", 0x00A1, 4, t2o_id) + struct.pack("<HH", 0x00B1, len(answer)) + answer
        return self.frame(CMD_SEND_UNIT, context, out)


# ---------------------------------------------------------------------------------------------------------
# Animations and main
# ---------------------------------------------------------------------------------------------------------

async def animate(ctl, tick_ms):
    entries = animations(ctl)
    tick_s = tick_ms / 1000.0
    schedule = [(ref, max(1, round(period / tick_ms)), fn) for ref, period, fn in entries]
    for ref, _, fn in schedule:
        ref.set(fn(0, 0.0))
    rates = sorted({every * tick_ms for _, every, _ in schedule})
    print("Animating {} values, tick {} ms, rates {} ms".format(len(schedule), tick_ms, rates), flush=True)
    loop = asyncio.get_running_loop()
    start = loop.time()
    tick = 0
    pause = ctl.ref("PauseSimulation")
    paused_since = None
    while True:
        # PauseSimulation freezes every value; the animation clock stops too, so values resume where they were.
        if pause.tag.buffer[pause.offset]:
            if paused_since is None:
                paused_since = loop.time()
                log.info("Simulation paused")
            await asyncio.sleep(0.1)
            continue
        if paused_since is not None:
            start += loop.time() - paused_since
            paused_since = None
            log.info("Simulation resumed")
        elapsed = loop.time() - start
        for ref, every, fn in schedule:
            if tick % every == 0:
                try:
                    ref.set(fn(tick // every, elapsed))
                except Exception:
                    log.exception("Animation failed for %s", ref.tag.name)
        tick += 1
        delay = start + tick * tick_s - loop.time()
        if delay < 0:
            tick = int((loop.time() - start) / tick_s) + 1  # fell behind: skip ticks rather than burst
            delay = start + tick * tick_s - loop.time()
        await asyncio.sleep(max(delay, 0))


async def serve():
    logging.basicConfig(level=os.environ.get("CIP_SIM_LOG", "INFO").upper(), stream=sys.stderr,
                        format="%(asctime)s %(levelname)s %(message)s")
    port = int(os.environ.get("CIP_SIM_PORT", "44818"))
    tick_ms = int(os.environ.get("CIP_SIM_FAST_TICK_MS", "10"))
    ctl = Controller(int(os.environ.get("CIP_SIM_BULK_TAGS", "3000")), int(os.environ.get("CIP_SIM_PROGRAMS", "20")))
    seed(ctl)
    tag_count = sum(len(s.tags) for s in ctl.scopes.values())
    print("Logix simulator: {} tags in {} scopes, {} templates".format(tag_count, len(ctl.scopes), len(ctl.templates.by_id)), flush=True)
    for line in ctl.describe():
        print(line, flush=True)
    cip = Cip(ctl)
    server = await asyncio.start_server(lambda r, w: Session(cip, r, w).run(), "0.0.0.0", port)
    if tick_ms > 0:
        asyncio.get_running_loop().create_task(animate(ctl, tick_ms))
    print("EtherNet/IP server ready on port {}".format(port), flush=True)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    random.seed(os.environ.get("CIP_SIM_SEED"))
    asyncio.run(serve())
