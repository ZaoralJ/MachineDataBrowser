"""The simulated controller: UDT definitions, controller/program tags, path resolution and animations."""

import math
import random
import time

from logix_model import ATOMICS, BOOL, Ref, Tag, Udt, string_type

A = ATOMICS
SINT, INT, DINT, LINT = A["SINT"], A["INT"], A["DINT"], A["LINT"]
USINT, UINT, UDINT, ULINT = A["USINT"], A["UINT"], A["UDINT"], A["ULINT"]
REAL, LREAL = A["REAL"], A["LREAL"]
BYTE, WORD, DWORD, LWORD = A["BYTE"], A["WORD"], A["DWORD"], A["LWORD"]

PROGRAM_TYPE = 0x1068      # "Program:<name>" entries in the controller listing
ROUTINE_TYPE = 0x1069      # "Routine:<name>" entries in program listings
TASK_TYPE = 0x1070         # "Task:<name>", "Map:<name>" and similar controller entries


class TemplateRegistry:
    def __init__(self):
        self.by_id = {}
        self._next = 0x100

    def add(self, udt):
        if udt.id is None:
            udt.id = self._next
            self._next += 1
        self.by_id[udt.id] = udt.layout()
        return udt


def build_types():
    reg = TemplateRegistry()
    t = {}

    def udt(name, fields, **kw):
        t[name] = reg.add(Udt(name, fields, **kw))
        return t[name]

    string = t["STRING"] = reg.add(string_type("STRING", 82, 0x0FCE))
    string20 = t["STRING20"] = reg.add(string_type("STRING20", 20, None))
    t["STRING_40"] = reg.add(string_type("STRING_40", 40, None))

    motor = udt("MOTOR_T", [
        ("Speed", REAL), ("Position", DINT), ("Running", BOOL), ("Faulted", BOOL), ("Name", string),
        ("Torque", REAL), ("ErrorCode", INT), ("Angles", REAL, 12)])
    robot = udt("ROBOT_T", [
        ("Speed", REAL), ("Position", DINT, 6), ("Active", BOOL), ("Joints", LREAL, 6),
        ("Program", string20), ("Cycle", UDINT)])
    conveyor = udt("CONVEYOR_T", [("Speed", REAL), ("Running", BOOL), ("Jam", BOOL), ("Count", ULINT)])
    udt("STATION_T", [
        ("Id", USINT), ("Name", string), ("Robot", robot), ("Conveyor", conveyor), ("Motors", motor, 3),
        ("Alarms", BOOL, 32), ("Status", WORD), ("Temps", REAL, 4), ("Enabled", BOOL)])

    detail = udt("DETAIL_T", [("Level", DINT)])
    state = udt("STATE_T", [("Code", DINT), ("Counter", DINT), ("Detail", detail)])
    udt("LINE_T", [
        ("Id", DINT), ("WorkCenter", DINT), ("Mode", INT), ("Flags", SINT), ("Total", LINT),
        ("Speed", REAL), ("State", state)])

    udt("TYPES_T", [
        ("B", BOOL), ("S", SINT), ("I", INT), ("D", DINT), ("L", LINT),
        ("US", USINT), ("UI", UINT), ("UD", UDINT), ("UL", ULINT),
        ("R", REAL), ("LR", LREAL), ("BY", BYTE), ("W", WORD), ("DW", DWORD), ("LW", LWORD),
        ("Str", string), ("Str20", string20)])
    udt("ARRAYS_T", [
        ("Bools", BOOL, 64), ("Sints", SINT, 16), ("Ints", INT, 16), ("Dints", DINT, 16), ("Lints", LINT, 4),
        ("Usints", USINT, 16), ("Uints", UINT, 8), ("Udints", UDINT, 8), ("Ulints", ULINT, 4),
        ("Reals", REAL, 16), ("Lreals", LREAL, 4), ("Bytes", BYTE, 8), ("Words", WORD, 8),
        ("Dwords", DWORD, 4), ("Lwords", LWORD, 2), ("Strings", string, 4), ("Motors", motor, 2)])
    udt("FLAGS_T", [("B{}".format(i), BOOL) for i in range(10)] + [("Word", DINT), ("B10", BOOL), ("B11", BOOL)])

    heater = udt("HEATER_T", [("Zones", REAL, 8), ("Setpoint", REAL), ("Enabled", BOOL)])
    tooling = udt("TOOLING_T", [("ToolId", string), ("Shots", DINT), ("Clamped", BOOL), ("Heater", heater)])
    press = udt("PRESS_T", [
        ("Tonnage", LREAL), ("StrokeCount", LINT), ("State", INT), ("Recipe", string),
        ("CycleTimes", REAL, 20), ("Alarms", DINT, 8), ("Tooling", tooling)])
    oven = udt("OVEN_T", [("Temperature", REAL), ("Profile", INT, 24), ("Running", BOOL)])
    hall = udt("HALL_T", [("Temperature", REAL), ("Humidity", REAL), ("DoorOpen", BOOL), ("Presses", press, 2), ("Oven", oven)])
    udt("PLANT_T", [("Name", string), ("SiteId", DINT), ("Online", BOOL), ("Halls", hall, 2)])

    axis = udt("AXIS_T", [
        ("Position", REAL), ("Velocity", REAL), ("Torque", REAL), ("Encoder", DINT),
        ("InPosition", BOOL), ("Enabled", BOOL)])
    cycle = udt("CYCLE_T", [("Counter", DINT), ("Phase", INT), ("TimestampUs", LINT), ("Busy", BOOL), ("Waveform", REAL, 32)])
    vision = udt("VISION_T", [("Score", REAL), ("PartOk", BOOL), ("LastCode", string), ("Offsets", LREAL, 3)])
    udt("HIGHSPEED_T", [("Axes", axis, 4), ("Cycle", cycle), ("Vision", vision)])

    udt("FAST_T", [("Counter", DINT), ("SineWave", REAL), ("Toggle", BOOL), ("Random", REAL)])
    udt("MEDIUM_T", [("Counter", DINT), ("Temperature", REAL), ("Pressure", REAL), ("Vibration", REAL)])
    udt("SLOW_T", [("Counter", DINT), ("Timestamp", DINT), ("Status", INT), ("UptimeSeconds", DINT)])

    udt("RECIPE_T", [("Name", string), ("Version", UINT), ("Setpoints", REAL, 200), ("Steps", DINT, 50)])
    udt("SERVO_T", [("Position", LREAL), ("Following", REAL), ("Faulted", BOOL), ("Mode", SINT)])
    udt("OUTPUT_T", [("TOTAL", DINT), ("OK", DINT), ("NOK", DINT)])
    udt("SINGLE_T", [("Value", DINT)])
    udt("BOOLS_ONLY_T", [("A", BOOL), ("B", BOOL), ("C", BOOL)])
    # Module-defined types carry colons in their names, like real I/O modules.
    udt("AB:1756_DI:I:0", [("Fault", DINT), ("Data", DINT)])
    udt("AB:1756_DO:O:0", [("Data", DINT)])
    return reg, t


class Scope:
    def __init__(self, name):
        self.name = name
        self.tags = {}

    def add(self, tag):
        self.tags[tag.name.lower()] = tag
        return tag

    def number(self):
        for instance_id, tag in enumerate(sorted(self.tags.values(), key=lambda t: t.name.lower()), start=1):
            tag.instance_id = instance_id
        self.by_instance = {tag.instance_id: tag for tag in self.tags.values()}

    def listing(self):
        return sorted(self.tags.values(), key=lambda t: t.instance_id)


class Controller:
    def __init__(self, bulk_tags=3000, programs=20):
        self.templates, self.types = build_types()
        self.scopes = {"": Scope("")}
        self._build(bulk_tags, programs)
        for scope in self.scopes.values():
            scope.number()

    # -- tag database -----------------------------------------------------------------------------------

    def _build(self, bulk_tags, programs):
        t = self.types
        c = self.scopes[""]
        add = lambda name, type_, *dims, **kw: c.add(Tag(name, type_, dims, **kw))

        for name, type_ in [("TestBool", BOOL), ("TestSint", SINT), ("TestInt", INT), ("TestDint", DINT),
                            ("TestLint", LINT), ("TestUsint", USINT), ("TestUint", UINT), ("TestUdint", UDINT),
                            ("TestUlint", ULINT), ("TestReal", REAL), ("TestLreal", LREAL), ("TestByte", BYTE),
                            ("TestWord", WORD), ("TestDword", DWORD), ("TestLword", LWORD),
                            ("TestString", t["STRING"]), ("TestString20", t["STRING20"]), ("TestString40", t["STRING_40"])]:
            add(name, type_)
        for name, type_, n in [("TestBoolArray", BOOL, 64), ("TestSintArray", SINT, 10), ("TestIntArray", INT, 10),
                               ("TestDintArray", DINT, 10), ("TestLintArray", LINT, 10), ("TestUdintArray", UDINT, 10),
                               ("TestRealArray", REAL, 10), ("TestLrealArray", LREAL, 10),
                               ("TestStringArray", t["STRING"], 5), ("TestString20Array", t["STRING20"], 3)]:
            add(name, type_, n)

        add("Matrix", DINT, 4, 5)
        add("Cube", REAL, 2, 3, 4)
        add("Grid", t["SINGLE_T"], 3, 3)
        add("BigDintArray", DINT, 5000)
        add("BigRealArray", REAL, 1500)
        add("BigStructArray", t["SINGLE_T"], 1200)

        add("Motor1", t["MOTOR_T"])
        add("Motor2", t["MOTOR_T"])
        add("Motors", t["MOTOR_T"], 4)
        add("Stations", t["STATION_T"], 6)
        add("Line", t["LINE_T"])
        add("Plant", t["PLANT_T"])
        add("Flags", t["FLAGS_T"])
        add("Types", t["TYPES_T"])
        add("Arrays", t["ARRAYS_T"])
        add("HighSpeed", t["HIGHSPEED_T"])
        add("Fast", t["FAST_T"])
        add("Medium", t["MEDIUM_T"])
        add("Slow", t["SLOW_T"])
        add("Recipes", t["RECIPE_T"], 3)
        add("ActiveRecipe", t["RECIPE_T"])
        add("Single", t["SINGLE_T"])
        add("BoolsOnly", t["BOOLS_ONLY_T"])
        add("Setpoints", REAL, 50)
        add("Totals", LINT, 10)
        add("Heartbeat", DINT)
        add("Recipe_Active", DINT)
        add("TagWithLongName_ThatIsFortyCharsLong_Ok", DINT)

        # Tags a browser should hide: module I/O, system and hidden tags, and non-tag entries.
        add("Local:1:I", t["AB:1756_DI:I:0"])
        add("Local:2:O", t["AB:1756_DO:O:0"])
        add("__HiddenCounter", DINT)
        add("SystemClock", DINT, system=True)
        add("Task:MainTask", None, raw_type=TASK_TYPE)
        add("Map:Local", None, raw_type=TASK_TYPE)

        for i in range(1, bulk_tags + 1):
            add("Bulk_{:04d}".format(i), DINT if i % 2 else REAL)

        def program(name, tags):
            scope = self.scopes.setdefault("program:" + name.lower(), Scope("Program:" + name))
            c.add(Tag("Program:" + name, None, raw_type=PROGRAM_TYPE))
            scope.add(Tag("Routine:MainRoutine", None, raw_type=ROUTINE_TYPE))
            for tag in tags:
                scope.add(tag)

        program("MainProgram", [Tag("Counter", DINT), Tag("Status", INT), Tag("Steps", t["STRING"], [3]),
                                Tag("Station", t["STATION_T"]), Tag("__Internal", DINT)])
        program("Motion", [Tag("Servo", t["SERVO_T"]), Tag("Servos", t["SERVO_T"], [4]), Tag("Ticks", DINT)])
        program("DATALOG", [Tag("OUTPUT", t["OUTPUT_T"])])
        program("Packaging", [Tag("Line", t["CONVEYOR_T"]), Tag("Enabled", BOOL), Tag("Mode", INT), Tag("Buffer", DINT, [32])])
        program("Safety", [Tag("EStop", BOOL), Tag("Guards", BOOL, [16]), Tag("Faults", DINT, [4])])
        program("Empty", [])
        for i in range(1, programs + 1):
            program("Line{:02d}".format(i), [Tag("Station", t["STATION_T"]), Tag("Counter", DINT), Tag("Speed", REAL)])

    # -- path resolution ----------------------------------------------------------------------------------

    def scope(self, name):
        return self.scopes.get(name.lower()) if name else self.scopes[""]

    def resolve(self, segments):
        """Resolve [("symbol", name) | ("element", index)] to a Ref, or None if the path is unknown."""
        segments = list(segments)
        if not segments or segments[0][0] != "symbol":
            return None
        scope = self.scopes[""]
        if segments[0][1].lower().startswith("program:") and len(segments) > 1:
            scope = self.scopes.get(segments[0][1].lower())
            segments = segments[1:]
            if scope is None or segments[0][0] != "symbol":
                return None
        tag = scope.tags.get(segments[0][1].lower())
        if tag is None or tag.type is None:
            return None

        type_, dims, offset = tag.type, list(tag.dims), 0
        bool_array, bool_count, bit = tag.bool_array, (tag.dims[0] if tag.bool_array else 0), None
        available = _product(dims)
        i = 1
        while i < len(segments):
            kind, value = segments[i]
            if kind == "element":
                indexes = []
                while i < len(segments) and segments[i][0] == "element":
                    indexes.append(segments[i][1])
                    i += 1
                if not dims or len(indexes) != len(dims):
                    return None
                if bool_array:
                    # Logix addresses BOOL arrays by DWORD: Bits[1] is the second 32-bit word.
                    words = (bool_count + 31) // 32
                    if indexes[0] >= words:
                        return None
                    offset += indexes[0] * 4
                    available = words - indexes[0]
                    bool_count -= indexes[0] * 32
                    dims = []
                    continue
                if any(x >= d for x, d in zip(indexes, dims)):
                    return None
                linear = 0
                for x, d in zip(indexes, dims):
                    linear = linear * d + x
                offset += linear * type_.size
                available = _product(dims) - linear
                dims = []
                continue
            if dims or not isinstance(type_, Udt):
                return None
            member = type_.member(value)
            if member is None:
                return None
            offset += member.offset
            type_, bit = member.type, member.bit
            bool_array, bool_count = member.bool_array, member.count if member.bool_array else 0
            dims = [member.count] if member.count else []
            available = _product(dims)
            i += 1
        if bool_array and dims:
            available = (bool_count + 31) // 32
        return Ref(tag, type_, offset, available, bit=bit, bool_array=bool_array, bool_count=bool_count)

    def ref(self, path):
        """Resolve a dotted tag path such as 'Program:Motion.Servos[2].Position' (used by the animations)."""
        segments = []
        for part in _split_path(path):
            name, _, rest = part.partition("[")
            segments.append(("symbol", name))
            if rest:
                segments += [("element", int(x)) for x in rest.rstrip("]").split(",")]
        ref = self.resolve(segments)
        if ref is None:
            raise KeyError(path)
        return ref

    def describe(self):
        for udt in sorted(self.templates.by_id.values(), key=lambda u: u.id):
            yield "  UDT 0x{:03X} {} ({} bytes, {} members)".format(udt.id, udt.name, udt.size, len(udt.members))


def _product(dims):
    n = 1
    for d in dims:
        n *= d
    return n


def _split_path(path):
    if path.lower().startswith("program:"):
        head, _, rest = path.partition(".")
        return [head] + rest.split(".")
    return path.split(".")


# ---------------------------------------------------------------------------------------------------------
# Seed values and animations
# ---------------------------------------------------------------------------------------------------------

def seed(ctl):
    s = lambda path, value: ctl.ref(path).set(value)
    s("TestBool", 1); s("TestSint", -12); s("TestInt", -1234); s("TestDint", 123456789)
    s("TestLint", 9007199254740993); s("TestUsint", 200); s("TestUint", 60000); s("TestUdint", 4000000000)
    s("TestUlint", 18000000000000000000); s("TestReal", 3.14159); s("TestLreal", 2.718281828459045)
    s("TestByte", 0xA5); s("TestWord", 0xBEEF); s("TestDword", 0xDEADBEEF); s("TestLword", 0x0123456789ABCDEF)
    s("TestString", "Hello Logix"); s("TestString20", "twenty chars max"); s("TestString40", "a forty character capacity string type")
    s("TestBoolArray", [i % 3 == 0 for i in range(64)])
    s("TestSintArray", list(range(-5, 5))); s("TestIntArray", [i * 100 for i in range(10)])
    s("TestDintArray", [i * 1000 for i in range(10)]); s("TestLintArray", [2 ** 40 + i for i in range(10)])
    s("TestUdintArray", [4000000000 + i for i in range(10)])
    s("TestRealArray", [i / 4 for i in range(10)]); s("TestLrealArray", [i / 3 for i in range(10)])
    s("TestStringArray", ["alpha", "beta", "gamma", "delta", "epsilon"]); s("TestString20Array", ["one", "two", "three"])
    s("Matrix", [r * 10 + c for r in range(4) for c in range(5)])
    s("Cube", [i * 0.5 for i in range(24)])
    for r in range(3):
        for c in range(3):
            s("Grid[{},{}].Value".format(r, c), r * 3 + c)
    s("BigDintArray", list(range(5000))); s("BigRealArray", [i * 0.1 for i in range(1500)])
    for i in range(1200):
        s("BigStructArray[{}].Value".format(i), i)

    for name, speed in (("Motor1", 1500.0), ("Motor2", 750.0)):
        s(name + ".Speed", speed); s(name + ".Name", name); s(name + ".Running", 1)
        s(name + ".Angles", [i * 30.0 for i in range(12)])
    for i in range(4):
        s("Motors[{}].Name".format(i), "Motor {}".format(i)); s("Motors[{}].Speed".format(i), 100.0 * (i + 1))
    for i in range(6):
        p = "Stations[{}]".format(i)
        s(p + ".Id", i + 1); s(p + ".Name", "Station {}".format(i + 1)); s(p + ".Enabled", i != 4)
        s(p + ".Robot.Program", "PRG_{:02d}".format(i)); s(p + ".Robot.Joints", [j * 15.0 for j in range(6)])
        s(p + ".Status", 0x0100 | i); s(p + ".Temps", [20.0 + i, 21.0 + i, 22.0 + i, 23.0 + i])
        s(p + ".Alarms", [j == i for j in range(32)])
        for m in range(3):
            s("{}.Motors[{}].Name".format(p, m), "S{}M{}".format(i + 1, m + 1))
    s("Line.Id", 961); s("Line.WorkCenter", 160367); s("Line.Mode", 3); s("Line.Flags", -5)
    s("Line.Total", 5000000000); s("Line.Speed", 12.5); s("Line.State.Code", 7); s("Line.State.Detail.Level", 3)

    s("Plant.Name", "Billund Moulding"); s("Plant.SiteId", 4201); s("Plant.Online", 1)
    for h in range(2):
        hp = "Plant.Halls[{}]".format(h)
        s(hp + ".Temperature", 21.5 + h); s(hp + ".Humidity", 44.0)
        s(hp + ".Oven.Profile", [20 + 10 * i for i in range(24)]); s(hp + ".Oven.Running", 1)
        for p in range(2):
            pp = "{}.Presses[{}]".format(hp, p)
            s(pp + ".Tonnage", 350.0 - 150 * p); s(pp + ".Recipe", "BRICK_2x4_RED" if p == 0 else "PLATE_1x6_BLUE")
            s(pp + ".StrokeCount", 7340032123 + p); s(pp + ".Tooling.ToolId", "T-{:04d}".format(42 + p))
            s(pp + ".Tooling.Heater.Setpoint", 230.0); s(pp + ".Tooling.Heater.Enabled", 1)
            s(pp + ".CycleTimes", [8.0 + 0.1 * i for i in range(20)]); s(pp + ".Alarms", [0, 0, 101, 0, 0, 0, 0, 999])

    for i in (0, 3, 8, 11):
        s("Flags.B{}".format(i), 1)
    s("Flags.Word", 0x5A5A)
    s("Types.B", 1); s("Types.S", -12); s("Types.I", -1234); s("Types.D", 123456789); s("Types.L", -(2 ** 60))
    s("Types.US", 255); s("Types.UI", 65535); s("Types.UD", 4294967295); s("Types.UL", 2 ** 64 - 1)
    s("Types.R", 3.14159); s("Types.LR", 2.718281828459045); s("Types.BY", 0x7F); s("Types.W", 0x1234)
    s("Types.DW", 0x89ABCDEF); s("Types.LW", 0xFEDCBA9876543210); s("Types.Str", "Hello CIP"); s("Types.Str20", "short")
    s("Arrays.Bools", [i % 2 == 0 for i in range(64)]); s("Arrays.Sints", list(range(-8, 8)))
    s("Arrays.Ints", [i * 100 for i in range(16)]); s("Arrays.Dints", [i * 100000 for i in range(16)])
    s("Arrays.Lints", [1, 2 ** 40, -(2 ** 40), 2 ** 62]); s("Arrays.Usints", list(range(240, 256)))
    s("Arrays.Uints", [65535 - i for i in range(8)]); s("Arrays.Udints", [4294967295 - i for i in range(8)])
    s("Arrays.Ulints", [2 ** 63 + i for i in range(4)]); s("Arrays.Reals", [i / 4 for i in range(16)])
    s("Arrays.Lreals", [0.1, 0.2, 0.3, 0.4]); s("Arrays.Bytes", list(range(8))); s("Arrays.Words", [0x1111 * i for i in range(8)])
    s("Arrays.Dwords", [0xFFFFFFFF, 0, 0x80000000, 1]); s("Arrays.Lwords", [2 ** 64 - 1, 0])
    s("Arrays.Strings", ["alpha", "beta", "gamma", "delta"])
    s("Arrays.Motors[0].Name", "Nested A"); s("Arrays.Motors[1].Name", "Nested B")
    for r in range(3):
        rp = "Recipes[{}]".format(r)
        s(rp + ".Name", "Recipe {}".format(r + 1)); s(rp + ".Version", r + 1)
        s(rp + ".Setpoints", [r * 1000 + i * 0.5 for i in range(200)]); s(rp + ".Steps", list(range(50)))
    s("ActiveRecipe.Name", "Default"); s("ActiveRecipe.Setpoints", [float(i) for i in range(200)])
    s("Setpoints", [float(i) for i in range(50)]); s("Totals", [i * 1000000000 for i in range(10)])
    s("Recipe_Active", 7); s("Single.Value", 42); s("BoolsOnly.B", 1)
    s("Local:1:I.Data", 0x0F0F); s("__HiddenCounter", 99)
    for i in range(1, 3001):
        try:
            s("Bulk_{:04d}".format(i), i)
        except KeyError:
            break
    s("Program:MainProgram.Steps", ["Init", "Run", "Stop"]); s("Program:MainProgram.Station.Name", "Main station")
    s("Program:DATALOG.OUTPUT.TOTAL", 1000); s("Program:DATALOG.OUTPUT.OK", 990); s("Program:DATALOG.OUTPUT.NOK", 10)
    s("Program:Packaging.Enabled", 1); s("Program:Packaging.Mode", 3); s("Program:Packaging.Buffer", list(range(32)))
    s("Program:Safety.Guards", [True] * 16); s("Program:Safety.Faults", [0, 3, 0, 7])


def animations(ctl):
    """(ref, period ms, fn(tick_count_at_that_rate, elapsed_s)) triples."""
    r = ctl.ref
    out = []

    def a(path, period, fn):
        out.append((r(path), period, fn))

    # 10 / 20 / 50 ms: motion axes, cycle data, vision results.
    for i in range(4):
        p, ph = "HighSpeed.Axes[{}]".format(i), i * math.pi / 4
        a(p + ".Position", 10, lambda n, e, ph=ph: 100.0 * math.sin(e * 2.0 + ph))
        a(p + ".Velocity", 10, lambda n, e, ph=ph: 200.0 * math.cos(e * 2.0 + ph))
        a(p + ".Encoder", 10, lambda n, e, i=i: (n * (37 + i)) % 2147483647)
        a(p + ".Torque", 20, lambda n, e: 12.0 + random.gauss(0, 0.8))
        a(p + ".InPosition", 50, lambda n, e, ph=ph: abs(math.sin(e * 2.0 + ph)) > 0.95)
        a(p + ".Enabled", 1000, lambda n, e: 1)
    a("HighSpeed.Cycle.Counter", 10, lambda n, e: n % 2147483647)
    a("HighSpeed.Cycle.TimestampUs", 10, lambda n, e: int(time.time() * 1_000_000))
    a("HighSpeed.Cycle.Phase", 20, lambda n, e: n % 8)
    a("HighSpeed.Cycle.Busy", 20, lambda n, e: n % 2)
    a("HighSpeed.Cycle.Waveform", 50, lambda n, e: [math.sin(e * 4.0 + i * math.pi / 16) for i in range(32)])
    a("HighSpeed.Vision.Score", 50, lambda n, e: round(random.uniform(0.8, 1.0), 4))
    a("HighSpeed.Vision.PartOk", 50, lambda n, e: random.random() < 0.97)
    a("HighSpeed.Vision.LastCode", 50, lambda n, e: "PART-{:08d}".format(n))
    a("HighSpeed.Vision.Offsets", 50, lambda n, e: [random.gauss(0, 0.05) for _ in range(3)])
    a("Program:Motion.Servo.Position", 10, lambda n, e: 360.0 * ((e * 0.5) % 1.0))
    a("Program:Motion.Ticks", 10, lambda n, e: n % 2147483647)
    a("Program:Motion.Servo.Following", 20, lambda n, e: random.gauss(0, 0.01))
    a("Program:Motion.Servo.Faulted", 50, lambda n, e: random.random() < 0.01)
    for i in range(4):
        a("Program:Motion.Servos[{}].Position".format(i), 20, lambda n, e, i=i: 90.0 * i + 45.0 * math.sin(e + i))
    for i in range(6):
        p = "Stations[{}]".format(i)
        a(p + ".Robot.Speed", 10, lambda n, e, i=i: 50.0 + 25.0 * math.sin(e * 3.0 + i))
        a(p + ".Robot.Position", 20, lambda n, e, i=i: [int(1000 * math.sin(e + i + j)) for j in range(6)])
        a(p + ".Robot.Cycle", 50, lambda n, e: n)
        a(p + ".Conveyor.Count", 50, lambda n, e, i=i: n * (i + 1))
        a(p + ".Conveyor.Running", 1000, lambda n, e, i=i: (n + i) % 5 != 0)
        a(p + ".Motors[0].Speed", 50, lambda n, e, i=i: 1450.0 + random.gauss(0, 5.0))

    # 100 ms - 10 s: the classic Fast / Medium / Slow groups and slow process values.
    a("Fast.Counter", 100, lambda n, e: n % 2147483647)
    a("Fast.SineWave", 100, lambda n, e: math.sin(e * 2 * math.pi))
    a("Fast.Toggle", 100, lambda n, e: n % 2)
    a("Fast.Random", 100, lambda n, e: random.uniform(0.0, 100.0))
    a("Medium.Counter", 1000, lambda n, e: n)
    a("Medium.Temperature", 1000, lambda n, e: 22.0 + 3.0 * math.sin(e * 0.1) + random.gauss(0, 0.2))
    a("Medium.Pressure", 1000, lambda n, e: 1013.0 + 5.0 * math.sin(e * 0.05) + random.gauss(0, 0.5))
    a("Medium.Vibration", 1000, lambda n, e: random.gauss(0.5, 0.1) + (3.0 if random.random() < 0.02 else 0.0))
    a("Slow.Counter", 10000, lambda n, e: n)
    a("Slow.Timestamp", 10000, lambda n, e: int(time.time()) % 2147483647)
    a("Slow.Status", 10000, lambda n, e: n % 6)
    a("Slow.UptimeSeconds", 1000, lambda n, e: int(e))
    a("Heartbeat", 1000, lambda n, e: n)
    a("Line.State.Counter", 500, lambda n, e: n)
    a("Plant.Halls[0].Temperature", 500, lambda n, e: 21.5 + math.sin(e * 0.05) + random.gauss(0, 0.05))
    a("Plant.Halls[1].Oven.Temperature", 500, lambda n, e: 180.0 + 5.0 * math.sin(e * 0.2))
    a("Plant.Halls[0].Presses[0].StrokeCount", 500, lambda n, e: 7340032123 + n)
    a("Plant.Halls[0].Presses[0].State", 5000, lambda n, e: [1, 2, 2, 2, 3][n % 5])
    a("Plant.Halls[0].Presses[0].Tooling.Heater.Zones", 1000, lambda n, e: [225.0 + i + math.sin(e * 0.1 + i) for i in range(8)])
    a("Plant.Halls[0].DoorOpen", 10000, lambda n, e: random.random() < 0.3)
    a("Flags.B1", 500, lambda n, e: n % 2)
    a("Flags.Word", 1000, lambda n, e: (0x5A5A << (n % 16)) & 0x7FFFFFFF)
    a("Types.R", 100, lambda n, e: math.pi * math.sin(e))
    a("Types.UL", 1000, lambda n, e: 2 ** 64 - 1 - n)
    a("Matrix[0,0]", 100, lambda n, e: n)
    a("Cube[1,2,3]", 100, lambda n, e: math.sin(e))
    a("BigDintArray[4999]", 100, lambda n, e: n)
    a("TestBoolArray", 1000, lambda n, e: [(i + n) % 3 == 0 for i in range(64)])
    a("Program:DATALOG.OUTPUT.TOTAL", 1000, lambda n, e: 1000 + n)
    a("Program:MainProgram.Counter", 100, lambda n, e: n)
    for i in range(1, 100):
        try:
            a("Program:Line{:02d}.Counter".format(i), 1000, lambda n, e, i=i: n * i)
        except KeyError:
            break
    return out
