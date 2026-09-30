"""In-memory Logix data model: atomic types, UDT templates, tags backed by raw bytes.

Every tag owns a bytearray laid out exactly like a Logix controller stores it, so whole-struct reads, array
slices, member reads and writes are plain byte-range operations. UDT layout follows Logix rules: scalars are
aligned to their size, arrays and nested structures to 4 (8 when they contain 64-bit members), BOOL members
are packed into hidden SINT hosts, and the struct size is rounded up to its alignment.
"""

import struct
import zlib

STRUCT_FLAG = 0x8000
SYSTEM_FLAG = 0x1000
DIM_SHIFT = 13
HIDDEN_HOST_PREFIX = "ZZZZZZZZZZ"


class Atomic:
    def __init__(self, name, code, size, fmt):
        self.name = name
        self.code = code
        self.size = size
        self.fmt = fmt
        self.alignment = size

    def pack(self, value):
        if self.name == "BOOL":
            return b"\x01" if value else b"\x00"
        if self.fmt in "fd":
            return struct.pack("<" + self.fmt, float(value))
        bits = self.size * 8
        value = int(value)
        if self.fmt.isupper():
            value &= (1 << bits) - 1
        else:
            value = (value + (1 << (bits - 1))) % (1 << bits) - (1 << (bits - 1))
        return struct.pack("<" + self.fmt, value)

    def unpack(self, data, offset=0):
        return struct.unpack_from("<" + self.fmt, data, offset)[0]

    def __repr__(self):
        return self.name


ATOMICS = {a.name: a for a in [
    Atomic("BOOL", 0xC1, 1, "B"),
    Atomic("SINT", 0xC2, 1, "b"), Atomic("INT", 0xC3, 2, "h"),
    Atomic("DINT", 0xC4, 4, "i"), Atomic("LINT", 0xC5, 8, "q"),
    Atomic("USINT", 0xC6, 1, "B"), Atomic("UINT", 0xC7, 2, "H"),
    Atomic("UDINT", 0xC8, 4, "I"), Atomic("ULINT", 0xC9, 8, "Q"),
    Atomic("REAL", 0xCA, 4, "f"), Atomic("LREAL", 0xCB, 8, "d"),
    Atomic("BYTE", 0xD1, 1, "B"), Atomic("WORD", 0xD2, 2, "H"),
    Atomic("DWORD", 0xD3, 4, "I"), Atomic("LWORD", 0xD4, 8, "Q"),
]}
DWORD = ATOMICS["DWORD"]
BOOL = ATOMICS["BOOL"]


def align_up(value, alignment):
    return (value + alignment - 1) // alignment * alignment


class Member:
    """A laid-out UDT member. `count` > 0 marks an array; `bit` is set for packed BOOLs."""

    def __init__(self, name, type_, count, offset, bit=None, hidden=False, bool_array=False):
        self.name = name
        self.type = type_
        self.count = count
        self.offset = offset
        self.bit = bit
        self.hidden = hidden
        self.bool_array = bool_array

    @property
    def type_code(self):
        if self.bool_array:
            return DWORD.code | (1 << DIM_SHIFT)
        code = self.type.code if isinstance(self.type, Atomic) else STRUCT_FLAG | self.type.id
        return code | (1 << DIM_SHIFT) if self.count else code

    @property
    def info(self):
        if self.bit is not None:
            return self.bit
        if self.bool_array:
            return (self.count + 31) // 32  # BOOL[n] members are DWORD arrays in the template
        return self.count


class Udt:
    """A UDT (or built-in structure such as STRING). Call `layout` once all nested types exist."""

    def __init__(self, name, fields, template_id=None, is_string=False):
        self.name = name
        self.fields = fields
        self.id = template_id
        self.is_string = is_string
        self.members = []
        self.size = 0
        self.alignment = 4
        self.handle = 0
        self.definition = b""

    def layout(self):
        members, offset, alignment = [], 0, 4
        host_offset, host_bits = None, 8
        for field in self.fields:
            name, type_, count = field[0], field[1], field[2] if len(field) > 2 else 0
            if type_ is BOOL and not count:
                if host_bits == 8:
                    host_offset, host_bits = offset, 0
                    members.append(Member("{}{}{}".format(HIDDEN_HOST_PREFIX, self.name, offset), ATOMICS["SINT"], 0, offset, hidden=True))
                    offset += 1
                members.append(Member(name, BOOL, 0, host_offset, bit=host_bits))
                host_bits += 1
                continue
            host_bits = 8
            if type_ is BOOL:
                # BOOL[n] members are stored and described as DWORD[ceil(n/32)].
                offset = align_up(offset, 4)
                members.append(Member(name, BOOL, count, offset, bool_array=True))
                offset += 4 * ((count + 31) // 32)
                continue
            elements = max(count, 1)
            item_alignment = type_.alignment if not count else max(type_.alignment, 4)
            offset = align_up(offset, item_alignment)
            alignment = max(alignment, type_.alignment)
            members.append(Member(name, type_, count, offset))
            offset += type_.size * elements
        self.members = members
        self.alignment = alignment
        self.size = align_up(offset, alignment)
        body = b"".join(struct.pack("<HHI", m.info, m.type_code, m.offset) for m in members)
        body += "{};n".format(self.name).encode("ascii") + b"\x00"
        body += b"".join(m.name.encode("ascii") + b"\x00" for m in members)
        # Clients read (definition_words * 4 - 23) bytes, so pad to exactly that length like a controller does.
        words = (len(body) + 23 + 3) // 4
        self.definition = body.ljust(words * 4 - 23, b"\x00")
        self.handle = zlib.crc32(body) & 0xFFFF
        return self

    @property
    def definition_words(self):
        return (len(self.definition) + 23) // 4

    def member(self, name):
        lowered = name.lower()
        return next((m for m in self.members if m.name.lower() == lowered), None)

    def __repr__(self):
        return self.name


def string_type(name, capacity, template_id):
    return Udt(name, [("LEN", ATOMICS["DINT"]), ("DATA", ATOMICS["SINT"], capacity)], template_id, is_string=True)


class Tag:
    """A controller- or program-scoped tag. `dims` holds up to three array dimensions."""

    def __init__(self, name, type_, dims=(), system=False, raw_type=None):
        self.name = name
        self.type = type_
        self.dims = list(dims)
        self.system = system
        self.raw_type = raw_type
        self.instance_id = 0
        self.bool_array = type_ is BOOL and bool(self.dims)
        if self.bool_array:
            self.buffer = bytearray(4 * ((self.dims[0] + 31) // 32))
        else:
            count = 1
            for d in self.dims:
                count *= d
            self.buffer = bytearray(type_.size * count if type_ is not None else 0)

    @property
    def symbol_type(self):
        if self.raw_type is not None:
            return self.raw_type
        if self.bool_array:
            return DWORD.code | (1 << DIM_SHIFT)
        code = self.type.code if isinstance(self.type, Atomic) else STRUCT_FLAG | self.type.id
        code |= len(self.dims) << DIM_SHIFT
        return code | (SYSTEM_FLAG if self.system else 0)

    @property
    def element_size(self):
        if self.type is None:
            return 0
        return 4 if self.bool_array else self.type.size


class Ref:
    """A resolved position inside a tag: the type found there, its byte offset and how many elements follow."""

    def __init__(self, tag, type_, offset, available, bit=None, bool_array=False, bool_count=0):
        self.tag = tag
        self.type = type_
        self.offset = offset
        self.available = available
        self.bit = bit
        self.bool_array = bool_array
        self.bool_count = bool_count

    @property
    def element_size(self):
        return 4 if self.bool_array else self.type.size

    @property
    def type_bytes(self):
        if self.bool_array:
            return struct.pack("<H", DWORD.code)
        if isinstance(self.type, Atomic):
            return struct.pack("<H", self.type.code)
        return struct.pack("<HH", 0x02A0, self.type.handle)

    # -- value helpers used by the animations ---------------------------------------------------------

    def set(self, value):
        buffer = self.tag.buffer
        if self.bit is not None:
            if value:
                buffer[self.offset] |= 1 << self.bit
            else:
                buffer[self.offset] &= ~(1 << self.bit) & 0xFF
        elif self.bool_array:
            words = [0] * ((self.bool_count + 31) // 32)
            for i, bit in enumerate(value[:self.bool_count]):
                if bit:
                    words[i // 32] |= 1 << (i % 32)
            buffer[self.offset:self.offset + 4 * len(words)] = b"".join(struct.pack("<I", w) for w in words)
        elif isinstance(self.type, Udt) and self.type.is_string:
            if isinstance(value, (list, tuple)):
                for i, text in enumerate(value[:self.available]):
                    self._set_string(self.offset + i * self.type.size, text)
            else:
                self._set_string(self.offset, value)
        elif isinstance(self.type, Atomic):
            if isinstance(value, (list, tuple)):
                data = b"".join(self.type.pack(v) for v in value[:self.available])
            else:
                data = self.type.pack(value)
            buffer[self.offset:self.offset + len(data)] = data
        else:
            raise TypeError("cannot assign a value to structure {}".format(self.type.name))

    def _set_string(self, offset, text):
        capacity = self.type.members[1].count
        data = str(text).encode("latin-1", "replace")[:capacity]
        struct.pack_into("<i", self.tag.buffer, offset, len(data))
        self.tag.buffer[offset + 4:offset + 4 + capacity] = data.ljust(capacity, b"\x00")
