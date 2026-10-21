"""MTConnect agent simulator (pure Python, no dependencies).

Serves the MTConnect REST API that the browser uses, from a simulated 3-axis mill and a lathe:
  GET /probe                 device model (MTConnectDevices)
  GET /current[?path=]       latest observation of every data item (MTConnectStreams)
  GET /sample?from=&count=[&path=]
                             observations from the ring buffer, oldest first
  GET /<device>/probe|current|sample   the same for one device
  PUT|POST /simulator/pause?value=true|false   freeze / continue every changing value (not part of MTConnect)

`path` accepts the XPath forms clients use for data items: //DataItem[@id="a" or @id='b'], //DataItem[@type="X"]
and //Components/<Type>; anything else is answered with INVALID_XPATH.

Environment:
  MTCONNECT_SIM_PORT       TCP port (default 5000)
  MTCONNECT_SIM_TICK_MS    fast tick in ms (default 100); slow values change every 10 ticks
  MTCONNECT_SIM_BUFFER     ring buffer size (default 131072)
  MTCONNECT_SIM_LOG        log level (default INFO)
"""

import datetime
import http.server
import logging
import math
import os
import random
import re
import socketserver
import threading
import time
import urllib.parse
import uuid
from xml.sax.saxutils import escape, quoteattr

VERSION = "2.2"
DEVICES_NS = f"urn:mtconnect.org:MTConnectDevices:{VERSION}"
STREAMS_NS = f"urn:mtconnect.org:MTConnectStreams:{VERSION}"
ERROR_NS = f"urn:mtconnect.org:MTConnectError:{VERSION}"
UNAVAILABLE = "UNAVAILABLE"

log = logging.getLogger("mtconnect")


def element_name(item_type):
    """MTConnect element names are the PascalCase form of the data item type (POSITION -> Position)."""
    return "".join(part.capitalize() for part in item_type.split("_"))


class DataItem:
    def __init__(self, id, category, type, name=None, sub_type=None, units=None, native_units=None,
                 representation=None, sample_count=None, coordinate_system=None):
        self.id = id
        self.category = category
        self.type = type
        self.name = name
        self.sub_type = sub_type
        self.units = units
        self.native_units = native_units
        self.representation = representation
        self.sample_count = sample_count
        self.coordinate_system = coordinate_system
        self.component = None
        self.device = None

    def xml(self, indent):
        attrs = [("category", self.category), ("id", self.id), ("type", self.type)]
        for key, value in (("name", self.name), ("subType", self.sub_type), ("units", self.units),
                           ("nativeUnits", self.native_units), ("representation", self.representation),
                           ("coordinateSystem", self.coordinate_system)):
            if value is not None:
                attrs.append((key, value))
        text = " ".join(f"{k}={quoteattr(v)}" for k, v in attrs)
        return f"{indent}<DataItem {text}/>"


class Component:
    def __init__(self, element, id, name=None, items=(), children=(), description=None, uuid_=None):
        self.element = element
        self.id = id
        self.name = name
        self.items = list(items)
        self.children = list(children)
        self.description = description
        self.uuid = uuid_

    def walk(self):
        yield self
        for child in self.children:
            yield from child.walk()

    def xml(self, indent=""):
        attrs = f"id={quoteattr(self.id)}"
        if self.name:
            attrs += f" name={quoteattr(self.name)}"
        if self.uuid:
            attrs += f" uuid={quoteattr(self.uuid)}"
        lines = [f"{indent}<{self.element} {attrs}>"]
        if self.description:
            lines.append(f"{indent}  <Description>{escape(self.description)}</Description>")
        if self.items:
            lines.append(f"{indent}  <DataItems>")
            lines.extend(item.xml(indent + "    ") for item in self.items)
            lines.append(f"{indent}  </DataItems>")
        if self.children:
            lines.append(f"{indent}  <Components>")
            lines.extend(child.xml(indent + "    ") for child in self.children)
            lines.append(f"{indent}  </Components>")
        lines.append(f"{indent}</{self.element}>")
        return "\n".join(lines)


def S(id, type, units, name=None, **kw):
    return DataItem(id, "SAMPLE", type, name=name, units=units, native_units=kw.pop("native_units", units), **kw)


def E(id, type, name=None, **kw):
    return DataItem(id, "EVENT", type, name=name, **kw)


def C(id, type, name=None, **kw):
    return DataItem(id, "CONDITION", type, name=name, **kw)


def build_devices():
    agent = Component("Agent", "agent", "Agent", uuid_="mdb-sim-agent", items=[
        E("agent_avail", "AVAILABILITY"),
        E("device_added", "DEVICE_ADDED"),
        E("agent_pause", "VARIABLE", "PauseSimulation"),
    ], description="MachineDataBrowser MTConnect simulator")

    vmc = Component("Device", "vmc", "VMC", uuid_="mdb-sim-vmc", description="3-axis vertical machining centre", items=[
        E("vmc_avail", "AVAILABILITY", "avail"),
        E("vmc_asset_chg", "ASSET_CHANGED"),
        E("vmc_mode", "OPERATING_MODE", "operatingMode"),
    ], children=[
        Component("Axes", "vmc_axes", "base", children=[
            Component("Linear", f"vmc_{a.lower()}", a, items=[
                S(f"vmc_{a.lower()}_pos", "POSITION", "MILLIMETER", f"{a}pos", sub_type="ACTUAL"),
                S(f"vmc_{a.lower()}_cmd", "POSITION", "MILLIMETER", f"{a}cmd", sub_type="COMMANDED"),
                S(f"vmc_{a.lower()}_load", "LOAD", "PERCENT", f"{a}load"),
                S(f"vmc_{a.lower()}_temp", "TEMPERATURE", "CELSIUS", f"{a}temp"),
                C(f"vmc_{a.lower()}_travel", "POSITION", f"{a}travel"),
            ]) for a in ("X", "Y", "Z")
        ] + [
            Component("Rotary", "vmc_s", "S", items=[
                S("vmc_s_speed", "ROTARY_VELOCITY", "REVOLUTION/MINUTE", "Sspeed", sub_type="ACTUAL"),
                S("vmc_s_ovr", "ROTARY_VELOCITY_OVERRIDE", "PERCENT", "Sovr"),
                S("vmc_s_load", "LOAD", "PERCENT", "Sload"),
                E("vmc_s_mode", "ROTARY_MODE", "Smode"),
                S("vmc_s_vib", "DISPLACEMENT", "MILLIMETER", "Svib", representation="TIME_SERIES"),
                C("vmc_s_cond", "SYSTEM", "Scond"),
            ]),
        ]),
        Component("Controller", "vmc_ctrl", "controller", items=[
            E("vmc_estop", "EMERGENCY_STOP", "estop"),
            E("vmc_ctrl_mode", "CONTROLLER_MODE", "mode"),
            C("vmc_comms", "COMMUNICATIONS", "comms"),
            C("vmc_logic", "LOGIC_PROGRAM", "logic"),
        ], children=[
            Component("Path", "vmc_path", "path", items=[
                E("vmc_exec", "EXECUTION", "execution"),
                E("vmc_program", "PROGRAM", "program"),
                E("vmc_line", "LINE_NUMBER", "line", sub_type="INCREMENTAL"),
                E("vmc_block", "BLOCK", "block"),
                E("vmc_part_count", "PART_COUNT", "partCount", sub_type="ALL"),
                E("vmc_tool", "TOOL_NUMBER", "tool"),
                S("vmc_feed", "PATH_FEEDRATE", "MILLIMETER/SECOND", "feed", sub_type="ACTUAL"),
                S("vmc_feed_ovr", "PATH_FEEDRATE_OVERRIDE", "PERCENT", "feedOvr", sub_type="PROGRAMMED"),
                S("vmc_path_pos", "PATH_POSITION", "MILLIMETER_3D", "pathPos", coordinate_system="WORK"),
                E("vmc_offsets", "WORK_OFFSET", "workOffset", representation="DATA_SET"),
                C("vmc_motion", "MOTION_PROGRAM", "motion"),
            ]),
        ]),
        Component("Systems", "vmc_systems", children=[
            Component("Coolant", "vmc_coolant", "coolant", items=[
                S("vmc_coolant_level", "FILL_LEVEL", "PERCENT", "level"),
                S("vmc_coolant_temp", "TEMPERATURE", "CELSIUS", "temp"),
                C("vmc_coolant_cond", "SYSTEM", "cond"),
            ]),
            Component("Electric", "vmc_electric", "electric", items=[
                S("vmc_power", "WATTAGE", "WATT", "power"),
                S("vmc_energy", "ACCUMULATED_TIME", "SECOND", "powerOnTime", sub_type="POWERED"),
            ]),
        ]),
        Component("Door", "vmc_door", "door", items=[E("vmc_door_state", "DOOR_STATE", "state")]),
        Component("Sensor", "vmc_sensor", "spare", items=[
            S("vmc_spare", "TEMPERATURE", "CELSIUS", "spareTemp"),
        ], description="Not wired: always UNAVAILABLE"),
    ])

    lathe = Component("Device", "lathe", "Lathe", uuid_="mdb-sim-lathe", description="2-axis turning centre", items=[
        E("lathe_avail", "AVAILABILITY", "avail"),
    ], children=[
        Component("Axes", "lathe_axes", "base", children=[
            Component("Linear", "lathe_x", "X", items=[S("lathe_x_pos", "POSITION", "MILLIMETER", "Xpos", sub_type="ACTUAL")]),
            Component("Linear", "lathe_z", "Z", items=[S("lathe_z_pos", "POSITION", "MILLIMETER", "Zpos", sub_type="ACTUAL")]),
            Component("Rotary", "lathe_c", "C", items=[
                S("lathe_c_speed", "ROTARY_VELOCITY", "REVOLUTION/MINUTE", "Cspeed", sub_type="ACTUAL"),
                E("lathe_chuck", "CHUCK_STATE", "chuck"),
            ]),
        ]),
        Component("Controller", "lathe_ctrl", "controller", children=[
            Component("Path", "lathe_path", "path", items=[
                E("lathe_exec", "EXECUTION", "execution"),
                E("lathe_program", "PROGRAM", "program"),
                E("lathe_part_count", "PART_COUNT", "partCount", sub_type="ALL"),
                C("lathe_system", "SYSTEM", "system"),
            ]),
        ]),
    ])

    devices = [agent, vmc, lathe]
    for device in devices:
        for component in device.walk():
            for item in component.items:
                item.component = component
                item.device = device
    return devices


def now_iso():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


class Agent:
    def __init__(self, buffer_size):
        self.devices = build_devices()
        self.items = {item.id: item for device in self.devices for c in device.walk() for item in c.items}
        self.instance_id = int(time.time())
        self.buffer_size = buffer_size
        self.buffer = []  # (sequence, item id, timestamp, value)
        self.first = 1
        self.next = 1
        self.latest = {}
        self.lock = threading.Lock()
        self.paused = False
        self.creation = now_iso()

    # Values: str for events and samples; (level, native_code, text) for conditions.
    def add(self, item_id, value, force=False):
        with self.lock:
            last = self.latest.get(item_id)
            if not force and last is not None and last[3] == value:
                return
            observation = (self.next, item_id, now_iso(), value)
            self.next += 1
            self.buffer.append(observation)
            self.latest[item_id] = observation
            if len(self.buffer) > self.buffer_size:
                drop = len(self.buffer) - self.buffer_size
                del self.buffer[:drop]
                self.first = self.buffer[0][0]

    def set_paused(self, paused):
        if paused != self.paused:
            self.paused = paused
            log.info("Simulation %s", "paused" if paused else "continued")
            self.add("agent_pause", "true" if paused else "false")

    def header(self, kind):
        attrs = {
            "creationTime": now_iso(),
            "sender": "machinedatabrowser-sim",
            "instanceId": str(self.instance_id),
            "version": f"{VERSION}.0.0",
            "bufferSize": str(self.buffer_size),
        }
        if kind == "devices":
            attrs["assetBufferSize"] = "1024"
            attrs["assetCount"] = "0"
        else:
            attrs["firstSequence"] = str(self.first)
            attrs["lastSequence"] = str(self.next - 1)
            attrs["nextSequence"] = str(self.next)
        return "<Header " + " ".join(f"{k}={quoteattr(v)}" for k, v in attrs.items()) + "/>"

    def probe(self, devices):
        body = "\n".join(d.xml("    ") for d in devices)
        return (f'<?xml version="1.0" encoding="UTF-8"?>\n<MTConnectDevices xmlns="{DEVICES_NS}">\n  '
                f'{self.header("devices")}\n  <Devices>\n{body}\n  </Devices>\n</MTConnectDevices>\n')

    def streams(self, observations, header):
        by_device = {}
        for seq, item_id, timestamp, value in observations:
            item = self.items[item_id]
            by_device.setdefault(item.device, {}).setdefault(item.component, []).append((seq, item, timestamp, value))
        lines = []
        for device in self.devices:
            components = by_device.get(device)
            if not components:
                continue
            lines.append(f'    <DeviceStream name={quoteattr(device.name)} uuid={quoteattr(device.uuid)}>')
            for component in device.walk():
                observations = components.get(component)
                if not observations:
                    continue
                name = f" name={quoteattr(component.name)}" if component.name else ""
                lines.append(f'      <ComponentStream component="{component.element}" componentId={quoteattr(component.id)}{name}>')
                for category, wrapper in (("SAMPLE", "Samples"), ("EVENT", "Events"), ("CONDITION", "Condition")):
                    group = [o for o in observations if o[1].category == category]
                    if group:
                        lines.append(f"        <{wrapper}>")
                        lines.extend("          " + self.observation(*o) for o in group)
                        lines.append(f"        </{wrapper}>")
                lines.append("      </ComponentStream>")
            lines.append("    </DeviceStream>")
        body = "\n".join(lines)
        return (f'<?xml version="1.0" encoding="UTF-8"?>\n<MTConnectStreams xmlns="{STREAMS_NS}">\n  {header}\n'
                f'  <Streams>\n{body}\n  </Streams>\n</MTConnectStreams>\n')

    @staticmethod
    def observation(seq, item, timestamp, value):
        attrs = f'dataItemId={quoteattr(item.id)} sequence="{seq}" timestamp="{timestamp}"'
        if item.name:
            attrs += f" name={quoteattr(item.name)}"
        if item.sub_type:
            attrs += f" subType={quoteattr(item.sub_type)}"
        if item.category == "CONDITION":
            level, code, text = value
            attrs += f" type={quoteattr(item.type)}"
            if code:
                attrs += f" nativeCode={quoteattr(code)}"
            return f"<{level} {attrs}>{escape(text)}</{level}>" if text else f"<{level} {attrs}/>"
        element = element_name(item.type)
        if item.representation == "TIME_SERIES":
            element += "TimeSeries"
            if value != UNAVAILABLE:
                attrs += f' sampleCount="{len(value.split())}" sampleRate="1000"'
        elif item.representation == "DATA_SET":
            element += "DataSet"
            if value != UNAVAILABLE:
                entries = [kv.split("=", 1) for kv in value.split()]
                attrs += f' count="{len(entries)}"'
                inner = "".join(f"<Entry key={quoteattr(k)}>{escape(v)}</Entry>" for k, v in entries)
                return f"<{element} {attrs}>{inner}</{element}>"
        return f"<{element} {attrs}>{escape(value)}</{element}>"

    def select(self, path):
        """Data item ids matching a path filter; None means every item."""
        if not path:
            return None
        path = path.strip()
        m = re.fullmatch(r"//DataItem\[(.+)\]", path)
        if m:
            terms = [t.strip() for t in re.split(r"\s+or\s+", m.group(1))]
            ids = set()
            for term in terms:
                t = re.fullmatch(r"@(id|type|name|category)\s*=\s*(['\"])(.*?)\2", term)
                if not t:
                    raise ValueError(path)
                key, value = t.group(1), t.group(3)
                ids.update(i.id for i in self.items.values()
                           if (i.id if key == "id" else i.type if key == "type" else i.name if key == "name" else i.category) == value)
            return ids
        m = re.fullmatch(r"//(?:Components/)?(\w+)", path)
        if m:
            return {i.id for i in self.items.values() if i.component.element == m.group(1) or i.component.id == m.group(1)}
        raise ValueError(path)

    def device_items(self, device_name):
        device = next((d for d in self.devices if device_name in (d.name, d.uuid)), None)
        if device is None:
            return None, None
        return device, {i.id for c in device.walk() for i in c.items}

    def error(self, code, text):
        return (f'<?xml version="1.0" encoding="UTF-8"?>\n<MTConnectError xmlns="{ERROR_NS}">\n  {self.header("error")}\n'
                f'  <Errors><Error errorCode="{code}">{escape(text)}</Error></Errors>\n</MTConnectError>\n')


class Simulation:
    """Moves the machines. The clock stops while paused, so values continue where they were."""

    def __init__(self, agent, tick_ms):
        self.agent = agent
        self.tick = tick_ms / 1000.0
        self.clock = 0.0
        self.ticks = 0
        self.parts = {"vmc": 0, "lathe": 0}
        self.rng = random.Random(42)

    def initial(self):
        a = self.agent
        for item in a.items.values():
            a.add(item.id, ("Unavailable", None, "") if item.category == "CONDITION" else UNAVAILABLE, force=True)
        a.add("agent_avail", "AVAILABLE")
        a.add("agent_pause", "false")
        a.add("device_added", "mdb-sim-lathe")
        for device in ("vmc", "lathe"):
            a.add(f"{device}_avail", "AVAILABLE")
        a.add("vmc_estop", "ARMED")
        a.add("vmc_ctrl_mode", "AUTOMATIC")
        a.add("vmc_mode", "AUTOMATIC")
        a.add("vmc_s_mode", "SPINDLE")
        a.add("vmc_program", "O1001 BRACKET")
        a.add("vmc_offsets", "G54=0,0,0 G55=120.5,-40,0 G56=-75.25,30,12")
        a.add("lathe_program", "O2001 SHAFT")
        a.add("lathe_chuck", "CLOSED")
        a.add("vmc_part_count", "0")
        a.add("lathe_part_count", "0")
        for cond in ("vmc_comms", "vmc_logic", "vmc_motion", "vmc_s_cond", "vmc_coolant_cond", "lathe_system",
                     "vmc_x_travel", "vmc_y_travel", "vmc_z_travel"):
            a.add(cond, ("Normal", None, ""))
        self.step()

    def run(self):
        next_tick = time.monotonic()
        while True:
            next_tick += self.tick
            time.sleep(max(0.0, next_tick - time.monotonic()))
            if self.agent.paused:
                continue
            self.clock += self.tick
            self.ticks += 1
            self.step()

    def step(self):
        a, t = self.agent, self.clock
        f = lambda v, d=3: f"{v:.{d}f}"
        cycle = 60.0  # one part per minute
        phase = (t % cycle) / cycle
        cutting = 0.1 < phase < 0.9
        x = 150 * math.sin(2 * math.pi * phase * 3)
        y = 100 * math.cos(2 * math.pi * phase * 2)
        z = -20 + 15 * math.cos(2 * math.pi * phase) if cutting else 50.0
        a.add("vmc_x_pos", f(x)); a.add("vmc_y_pos", f(y)); a.add("vmc_z_pos", f(z))
        a.add("vmc_x_cmd", f(x + 0.002)); a.add("vmc_y_cmd", f(y - 0.001)); a.add("vmc_z_cmd", f(z))
        a.add("vmc_path_pos", f"{f(x)} {f(y)} {f(z)}")
        speed = 8000 + 400 * math.sin(t / 3) if cutting else 0
        a.add("vmc_s_speed", f(speed, 1))
        a.add("vmc_feed", f(25 + 5 * math.sin(t), 2) if cutting else "0.00")
        a.add("vmc_exec", "ACTIVE" if cutting else "READY")
        a.add("vmc_line", str(int(phase * 500) * 10))
        a.add("vmc_block", f"G01 X{x:.3f} Y{y:.3f} Z{z:.3f} F{1500}")
        a.add("vmc_tool", str(1 + int(phase * 4)))
        lx = 0.4 * math.sin(2 * math.pi * phase)
        lz = -200 * phase
        a.add("lathe_x_pos", f(25 + 10 * lx)); a.add("lathe_z_pos", f(lz))
        a.add("lathe_c_speed", f(1200 + 50 * math.sin(t), 1) if cutting else "0.0")
        a.add("lathe_exec", "ACTIVE" if cutting else "READY")

        if self.ticks % 10 == 0:  # slow values
            for axis in ("x", "y", "z"):
                a.add(f"vmc_{axis}_load", f(20 + 15 * abs(math.sin(t / 5 + len(axis))), 1))
                a.add(f"vmc_{axis}_temp", f(31 + 2 * math.sin(t / 60), 1))
            a.add("vmc_s_load", f(35 + 20 * abs(math.sin(t / 4)), 1) if cutting else "0.0")
            a.add("vmc_s_ovr", "100")
            a.add("vmc_feed_ovr", "100" if phase < 0.5 else "80")
            a.add("vmc_s_vib", " ".join(f(0.01 * math.sin(t * 50 + i) + self.rng.uniform(-0.002, 0.002), 4) for i in range(10)))
            level = 80 - 30 * ((t / 600) % 1)
            a.add("vmc_coolant_level", f(level, 1))
            a.add("vmc_coolant_temp", f(22 + math.sin(t / 120), 1))
            a.add("vmc_coolant_cond", ("Warning", "LOW", "Coolant level low") if level < 55 else ("Normal", None, ""))
            a.add("vmc_power", f(3500 + 2500 * (1 if cutting else 0) + 100 * math.sin(t), 0))
            a.add("vmc_energy", f(t, 0))
            door = "OPEN" if phase > 0.95 else "CLOSED"
            a.add("vmc_door_state", door)
            a.add("lathe_chuck", "OPEN" if phase > 0.95 else "CLOSED")
            # A spindle overtemp fault for 5 s of every 2 minutes.
            fault = (t % 120) > 115
            a.add("vmc_s_cond", ("Fault", "SP-OT", "Spindle over temperature") if fault else ("Normal", None, ""))
            a.add("lathe_system", ("Warning", "1042", "Tailstock pressure low") if (t % 90) > 80 else ("Normal", None, ""))

        part = int(t // cycle)
        if part != self.parts["vmc"]:
            self.parts["vmc"] = part
            a.add("vmc_part_count", str(part))
            a.add("lathe_part_count", str(part * 2))


class Handler(http.server.BaseHTTPRequestHandler):
    agent: Agent = None
    protocol_version = "HTTP/1.1"

    def log_message(self, format, *args):
        log.debug("%s %s", self.address_string(), format % args)

    def send(self, status, body, content_type="application/xml"):
        data = body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", f"{content_type}; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_PUT(self):
        self.do_POST()

    def do_POST(self):
        url = urllib.parse.urlsplit(self.path)
        if url.path.rstrip("/") != "/simulator/pause":
            self.send(404, "not found\n", "text/plain")
            return
        query = urllib.parse.parse_qs(url.query)
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length).decode().strip() if length else ""
        value = (query.get("value", [body])[0] or "true").lower()
        self.agent.set_paused(value in ("true", "1", "yes", "on"))
        self.send(200, f"{str(self.agent.paused).lower()}\n", "text/plain")

    def do_GET(self):
        a = self.agent
        url = urllib.parse.urlsplit(self.path)
        query = {k: v[-1] for k, v in urllib.parse.parse_qs(url.query).items()}
        parts = [p for p in url.path.split("/") if p]
        request = parts[-1] if parts else "probe"
        device_name = parts[0] if len(parts) == 2 else None
        if len(parts) > 2 or request not in ("probe", "current", "sample"):
            if len(parts) == 1 and a.device_items(parts[0])[0] is not None:
                request, device_name = "probe", parts[0]
            else:
                self.send(400, a.error("UNSUPPORTED", f"Unsupported request {url.path}"))
                return

        scope = None
        devices = a.devices
        if device_name is not None:
            device, scope = a.device_items(urllib.parse.unquote(device_name))
            if device is None:
                self.send(404, a.error("NO_DEVICE", f"Could not find the device '{device_name}'"))
                return
            devices = [device]

        if request == "probe":
            self.send(200, a.probe(devices))
            return

        try:
            selected = a.select(query.get("path"))
        except ValueError:
            self.send(400, a.error("INVALID_XPATH", f"The path could not be parsed: {query.get('path')}"))
            return
        wanted = lambda item_id: (selected is None or item_id in selected) and (scope is None or item_id in scope)

        with a.lock:
            header = a.header("streams")
            if request == "current":
                observations = sorted((o for o in a.latest.values() if wanted(o[1])), key=lambda o: o[0])
                self.send(200, a.streams(observations, header))
                return

            try:
                count = int(query.get("count", "100"))
                start = int(query.get("from", str(a.first)))
            except ValueError:
                self.send(400, a.error("INVALID_REQUEST", "from and count must be integers"))
                return
            if count <= 0 or count > a.buffer_size:
                self.send(400, a.error("OUT_OF_RANGE", f"'count' must be between 1 and {a.buffer_size}"))
                return
            if start < a.first or start > a.next:
                self.send(400, a.error("OUT_OF_RANGE", f"'from' must be between {a.first} and {a.next}"))
                return
            offset = start - a.first
            result = []
            last = start - 1
            for observation in a.buffer[offset:]:
                last = observation[0]
                if wanted(observation[1]):
                    result.append(observation)
                    if len(result) >= count:
                        break
            # nextSequence tells the client where to continue: after the last observation looked at.
            header = re.sub(r'nextSequence="\d+"', f'nextSequence="{last + 1}"', header)
            self.send(200, a.streams(result, header))


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def main():
    logging.basicConfig(level=os.environ.get("MTCONNECT_SIM_LOG", "INFO"), format="%(asctime)s %(name)s %(message)s")
    port = int(os.environ.get("MTCONNECT_SIM_PORT", "5000"))
    tick = float(os.environ.get("MTCONNECT_SIM_TICK_MS", "100"))
    agent = Agent(int(os.environ.get("MTCONNECT_SIM_BUFFER", "131072")))
    simulation = Simulation(agent, tick)
    simulation.initial()
    threading.Thread(target=simulation.run, daemon=True).start()
    Handler.agent = agent
    log.info("MTConnect agent on http://0.0.0.0:%d (instance %d, %d data items, tick %g ms)",
             port, agent.instance_id, len(agent.items), tick)
    Server(("0.0.0.0", port), Handler).serve_forever()


if __name__ == "__main__":
    main()
