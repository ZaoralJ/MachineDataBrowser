"""Publishes test traffic to the local Mosquitto broker for OpcUaBrowser.

Topics (all under the default '#' subscription):
  plant/...           plain values: numbers, booleans, text; plant/hall1 also carries its own payload
  fast/<rate>/...     values changing every 10 / 20 / 50 ms (MQTT_SIM_FAST_MS is the base tick)
  machines/<m>/status JSON documents with nested objects, arrays and null
  config/..., plant/info/...  retained messages (visible immediately after connecting)
  raw/...             raw binary payloads: raw/blob (bytes), raw/int32 and raw/float64 (little-endian numbers)
  events/...          CloudEvents 1.0: events/press1/structured (JSON envelope, application/cloudevents+json)
                      and events/press1/binary (MQTT 5 user properties ce_* + JSON data)
  deep/l1/.../l8/value  deep topic tree
  bulk/sensor/NNNN    many topics (MQTT_SIM_BULK, default 500)
  acme/...            Unified Namespace (ISA-95: enterprise/site/area/line/station): per station state, process
                      values, counters, OEE, alarms and retained metadata; per line order, shift and KPIs;
                      per site energy (MQTT_SIM_UNS_LINES lines x press/robot/conveyor, default 4)
  spBv1.0/...         Sparkplug B: group Plant1, edge nodes Edge1 (devices Press1, Press2) and Edge2;
                      Press2 dies and is reborn every MQTT_SIM_DEATH_PERIOD_S seconds
"""

import json
import math
import os
import random
import struct
import time

import uuid

import paho.mqtt.client as mqtt
from paho.mqtt.packettypes import PacketTypes
from paho.mqtt.properties import Properties

HOST = "127.0.0.1"
TICK_MS = int(os.environ.get("MQTT_SIM_FAST_MS", "10"))
BULK = int(os.environ.get("MQTT_SIM_BULK", "500"))
DEATH_PERIOD_S = float(os.environ.get("MQTT_SIM_DEATH_PERIOD_S", "30"))
# Edge nodes publish their births only at start (and on a rebirth request). Re-publish them periodically so a
# browser that connects later learns metric names and aliases without sending a command.
REBIRTH_S = float(os.environ.get("MQTT_SIM_REBIRTH_S", "15"))
UNS_LINES = int(os.environ.get("MQTT_SIM_UNS_LINES", "4"))

# ------------------------------------------------------------------------------------------------ Unified Namespace

UNS_SITE = "acme/billund"
UNS_AREA = UNS_SITE + "/moulding"
STATIONS = {
    "press": {"model": "Engel e-mac 180", "vendor": "Engel", "process": {"temperature": (220, 6, "°C"), "pressure": (180, 12, "bar"), "cycleTime": (8.2, 0.3, "s")}},
    "robot": {"model": "KUKA KR 10", "vendor": "KUKA", "process": {"speed": (1.2, 0.2, "m/s"), "torque": (35, 4, "Nm"), "gripperVacuum": (-0.8, 0.05, "bar")}},
    "conveyor": {"model": "Bosch VarioFlow", "vendor": "Bosch Rexroth", "process": {"speed": (0.5, 0.05, "m/s"), "motorCurrent": (3.1, 0.3, "A"), "load": (40, 15, "%")}},
}
STATES = ["Running", "Running", "Running", "Running", "Idle", "Setup", "Down"]
DOWN_REASONS = ["Material shortage", "Mould change", "Robot fault", "Quality check", "Jam"]
PRODUCTS = ["Brick 2x4 red", "Plate 1x6 blue", "Tile 2x2 yellow", "Slope 45° grey"]


class UnsStation:
    def __init__(self, line, name, index):
        self.base = f"{UNS_AREA}/line{line}/{name}"
        self.kind = STATIONS[name]
        self.phase = line * 1.7 + index
        self.good = 0
        self.bad = 0
        self.state = "Running"
        self.alarms = []

    def retained(self, pub, line):
        pub(f"{self.base}/_meta", json.dumps({
            "assetId": f"L{line}-{self.base.rsplit('/', 1)[1].upper()}",
            "vendor": self.kind["vendor"], "model": self.kind["model"],
            "installed": f"20{18 + line % 5}-0{1 + line % 9}-15",
            "units": {k: v[2] for k, v in self.kind["process"].items()},
        }), retain=True)

    def fast(self, pub, t):
        for name, (mean, spread, _) in self.kind["process"].items():
            value = mean + spread * math.sin(t / 3 + self.phase) + random.gauss(0, spread / 10) if self.state == "Running" else mean * 0.1
            pub(f"{self.base}/process/{name}", f"{value:.3f}")

    def slow(self, pub, t):
        state = STATES[int(t / 20 + self.phase) % len(STATES)]
        if state != self.state:
            self.state = state
            pub(f"{self.base}/state", state, retain=True)
            pub(f"{self.base}/state/reason", random.choice(DOWN_REASONS) if state == "Down" else "", retain=True)
            pub(f"{self.base}/state/since", time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), retain=True)
        if state == "Running":
            self.good += random.randint(1, 3)
            self.bad += 1 if random.random() < 0.03 else 0
        pub(f"{self.base}/counters/good", str(self.good))
        pub(f"{self.base}/counters/bad", str(self.bad))
        pub(f"{self.base}/counters/total", str(self.good + self.bad))
        alarms = [{"code": 4711, "text": "Hydraulic pressure low", "severity": "warning"}] if int(t / 13 + self.phase) % 5 == 0 else []
        if state == "Down":
            alarms.append({"code": 1001, "text": "Station stopped", "severity": "error"})
        if alarms != self.alarms:
            self.alarms = alarms
            pub(f"{self.base}/alarms/active", json.dumps(alarms), retain=True)
            pub(f"{self.base}/alarms/count", str(len(alarms)), retain=True)

    def oee(self, pub, t):
        availability = 0.85 + 0.1 * math.sin(t / 60 + self.phase)
        performance = 0.9 + 0.05 * math.cos(t / 45 + self.phase)
        quality = self.good / max(1, self.good + self.bad)
        pub(f"{self.base}/kpi/oee", json.dumps({
            "availability": round(availability, 4), "performance": round(performance, 4), "quality": round(quality, 4),
            "oee": round(availability * performance * quality, 4), "window": "PT5M",
        }))


def uns_line_retained(pub, line):
    pub(f"{UNS_AREA}/line{line}/_meta", json.dumps({"name": f"Moulding line {line}", "isa95Level": "WorkCenter", "stations": list(STATIONS)}), retain=True)
    pub(f"{UNS_AREA}/line{line}/shift", json.dumps({"name": ["Early", "Late", "Night"][line % 3], "crew": line % 4 + 1, "supervisor": f"Operator {line:02d}"}), retain=True)


def uns_line_order(pub, line, t):
    order = int(t / 120) + line * 1000
    pub(f"{UNS_AREA}/line{line}/order", json.dumps({
        "orderId": f"PO-{order:06d}", "product": PRODUCTS[(order + line) % len(PRODUCTS)],
        "quantity": 50000, "produced": int(t * 7 * line) % 50000, "due": time.strftime("%Y-%m-%d", time.gmtime(time.time() + 86400)),
    }), retain=True)

# ------------------------------------------------------------------------------------------------ Sparkplug B encoding

INT8, INT16, INT32, INT64, UINT8, UINT16, UINT32, UINT64 = 1, 2, 3, 4, 5, 6, 7, 8
FLOAT, DOUBLE, BOOLEAN, STRING, DATETIME = 9, 10, 11, 12, 13


def varint(value):
    value &= (1 << 64) - 1
    out = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        if value:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            return bytes(out)


def field_varint(number, value):
    return varint(number << 3) + varint(value)


def field_bytes(number, data):
    return varint(number << 3 | 2) + varint(len(data)) + data


def metric(name, alias, datatype, value, timestamp):
    body = b""
    if name is not None:
        body += field_bytes(1, name.encode())
    body += field_varint(2, alias) + field_varint(3, timestamp) + field_varint(4, datatype)
    if datatype in (INT8, INT16, INT32, UINT8, UINT16, UINT32):
        body += field_varint(10, int(value) & 0xFFFFFFFF)
    elif datatype in (INT64, UINT64, DATETIME):
        body += field_varint(11, int(value))
    elif datatype == FLOAT:
        body += varint(12 << 3 | 5) + struct.pack("<f", value)
    elif datatype == DOUBLE:
        body += varint(13 << 3 | 1) + struct.pack("<d", value)
    elif datatype == BOOLEAN:
        body += field_varint(14, 1 if value else 0)
    elif datatype == STRING:
        body += field_bytes(15, str(value).encode())
    return body


def payload(metrics, seq):
    now = int(time.time() * 1000)
    body = field_varint(1, now)
    for m in metrics:
        body += field_bytes(2, metric(*m, now))
    return body + field_varint(3, seq % 256)


class Device:
    """Metric definitions: name -> (alias, datatype, value function)."""

    def __init__(self, base_alias, phase):
        self.metrics = {
            "Temperature": (base_alias + 1, DOUBLE, lambda t: 200 + 20 * math.sin(t + phase)),
            "Pressure": (base_alias + 2, FLOAT, lambda t: 5 + math.cos(t / 3 + phase)),
            "Running": (base_alias + 3, BOOLEAN, lambda t: int(t / 5) % 4 != 3),
            "Count": (base_alias + 4, INT64, lambda t: int(t * 10)),
            "State": (base_alias + 5, STRING, lambda t: ["Idle", "Heating", "Running", "Cooling"][int(t / 5) % 4]),
            "Motor/Speed": (base_alias + 6, DOUBLE, lambda t: 1450 + 30 * math.sin(t * 2 + phase)),
            "Motor/Current": (base_alias + 7, INT32, lambda t: int(12 + 3 * math.sin(t))),
            "Config/Recipe": (base_alias + 8, STRING, lambda t: "BRICK_2x4"),
            "Types/Int8": (base_alias + 9, INT8, lambda t: -12),
            "Types/Int16": (base_alias + 10, INT16, lambda t: -1234),
            "Types/UInt8": (base_alias + 11, UINT8, lambda t: 200),
            "Types/UInt32": (base_alias + 12, UINT32, lambda t: 4000000000),
            "Types/DateTime": (base_alias + 13, DATETIME, lambda t: int(time.time() * 1000)),
        }

    def birth(self, t):
        return [(name, alias, dtype, fn(t)) for name, (alias, dtype, fn) in self.metrics.items()]

    def data(self, t, names):
        # DATA messages use aliases only (no names), as Sparkplug allows after the birth.
        return [(None, self.metrics[n][0], self.metrics[n][1], self.metrics[n][2](t)) for n in names]


# ------------------------------------------------------------------------------------------------ publisher


def main():
    group, edge1, edge2 = "Plant1", "Edge1", "Edge2"
    ndeath = field_varint(1, int(time.time() * 1000)) + field_bytes(2, metric("bdSeq", 1, INT64, 0, int(time.time() * 1000)))

    # MQTT 5, so binary-mode CloudEvents can carry their attributes as user properties.
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="opcuabrowser-sim", protocol=mqtt.MQTTv5)
    client.will_set(f"spBv1.0/{group}/NDEATH/{edge1}", ndeath, qos=0, retain=False)
    for _ in range(100):
        try:
            client.connect(HOST, 1883)
            break
        except OSError:
            time.sleep(0.1)
    client.loop_start()

    pub = lambda topic, value, retain=False: client.publish(topic, value, qos=0, retain=retain)

    def cloud_events(t, cycle):
        data = {"cycle": cycle, "durationMs": round(850 + 40 * math.sin(t), 1), "ok": cycle % 7 != 0}
        attributes = {
            "specversion": "1.0",
            "id": str(uuid.uuid4()),
            "source": "urn:plant:hall1:press1",
            "type": "com.example.press.cycle.completed",
            "time": time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime()) + f".{int(time.time() * 1000) % 1000:03d}Z",
        }
        # Structured mode: attributes and data in one JSON envelope.
        structured = Properties(PacketTypes.PUBLISH)
        structured.ContentType = "application/cloudevents+json"
        client.publish("events/press1/structured", json.dumps({**attributes, "datacontenttype": "application/json", "data": data}), qos=0, properties=structured)
        # Binary mode: the payload is the data; attributes travel as MQTT 5 user properties (CloudEvents MQTT binding).
        binary = Properties(PacketTypes.PUBLISH)
        binary.ContentType = "application/json"
        binary.UserProperty = [(k, v) for k, v in attributes.items()]
        client.publish("events/press1/binary", json.dumps(data), qos=0, properties=binary)

    # Retained messages, a topic with its own payload and children, binary and a deep tree.
    pub("config/line1", json.dumps({"name": "Line 1", "cycleMs": 850, "stations": ["Press", "Oven", "Pack"], "limits": {"min": 10, "max": 90}}), retain=True)
    pub("plant/info/site", "Billund Moulding", retain=True)
    pub("plant/info/version", "1.2.3", retain=True)
    pub("plant/hall1", "Hall 1", retain=True)
    pub("deep/l1/l2/l3/l4/l5/l6/l7/l8/value", "42", retain=True)
    pub("text/with spaces/and-üñíçødé", "Special characters in topic names", retain=True)
 # (An empty retained payload would delete the retained message, so no "empty" topic here.)
    for i in range(1, BULK + 1):
        pub(f"bulk/sensor/{i:04d}", f"{i}", retain=True)

    uns = [UnsStation(line, name, i) for line in range(1, UNS_LINES + 1) for i, name in enumerate(STATIONS)]
    pub(f"{UNS_SITE}/_meta", json.dumps({"enterprise": "ACME Toys", "site": "Billund", "timezone": "Europe/Copenhagen", "areas": ["moulding"]}), retain=True)
    pub(f"{UNS_AREA}/_meta", json.dumps({"name": "Moulding hall", "isa95Level": "Area", "lines": UNS_LINES}), retain=True)
    for line in range(1, UNS_LINES + 1):
        uns_line_retained(pub, line)
        uns_line_order(pub, line, 0)
    for station in uns:
        station.retained(pub, int(station.base.split("/line")[1].split("/")[0]))
        station.slow(pub, 0)

    devices = {"Press1": Device(100, 0.0), "Press2": Device(200, 1.5)}
    node_metrics = [("bdSeq", 1, INT64, 0), ("Node Control/Rebirth", 2, BOOLEAN, False), ("Properties/Hardware", 3, STRING, "OpcUaBrowser simulator")]
    seq = 0

    def send(kind, edge, device, metrics):
        nonlocal seq
        topic = f"spBv1.0/{group}/{kind}/{edge}" + (f"/{device}" if device else "")
        client.publish(topic, payload(metrics, seq), qos=0)
        seq += 1

    start = time.time()
    send("NBIRTH", edge1, None, node_metrics)
    for name, device in devices.items():
        send("DBIRTH", edge1, name, device.birth(0))
    edge2_birth = [("Uptime", 10, INT64, 0), ("Temperature", 11, DOUBLE, 21.5)]
    send("NBIRTH", edge2, None, edge2_birth)

    print("MQTT simulator ready (mqtt://localhost:1883, ws://localhost:9001)", flush=True)

    tick = 0
    press2_dead_since = None
    tick_s = TICK_MS / 1000.0
    while True:
        t = time.time() - start
        every = lambda ms: tick % max(1, round(ms / TICK_MS)) == 0

        pub("fast/10ms/counter", str(tick))
        if every(20):
            pub("fast/20ms/sine", f"{math.sin(t * 2 * math.pi):.4f}")
        if every(50):
            pub("fast/50ms/noise", f"{random.gauss(0, 1):.4f}")
            send("DDATA", edge1, "Press1", devices["Press1"].data(t, ["Temperature", "Motor/Speed"]))
            if press2_dead_since is None:
                send("DDATA", edge1, "Press2", devices["Press2"].data(t, ["Temperature", "Motor/Speed"]))
        if every(250):
            for station in uns:
                station.fast(pub, t)
        if every(100):
            pub("plant/hall1/press1/temperature", f"{220 + 5 * math.sin(t / 2):.2f}")
            pub("plant/hall1/press2/temperature", f"{180 + 5 * math.cos(t / 3):.2f}")
            pub("plant/hall1/press1/counter", str(int(t * 10)))
        if every(200):
            for m, phase in (("m1", 0.0), ("m2", 2.0)):
                pub(f"machines/{m}/status", json.dumps({
                    "state": ["Idle", "Run", "Run", "Stop"][int(t / 7 + phase) % 4],
                    "speed": round(1450 + 50 * math.sin(t + phase), 2),
                    "temperature": {"bearing": round(55 + math.sin(t / 5), 2), "motor": round(61 + math.cos(t / 4), 2)},
                    "alarms": [0, 101] if int(t / 10) % 2 else [],
                    "ok": int(t) % 9 != 0,
                    "operator": None,
                }))
        if every(1000):
            pub("plant/hall1/press1/pressure", f"{5 + math.sin(t):.3f}")
            pub("plant/hall1/press1/running", "true" if int(t / 2) % 2 == 0 else "false")
            pub("plant/hall1/press1/state", ["Idle", "Heating", "Running"][int(t / 5) % 3])
            pub("raw/blob", bytes([0xFF, 0xFE, 0x00, int(t) % 256, 0x80, 0x81]))
            pub("raw/int32", struct.pack("<i", int(t)))
            pub("raw/float64", struct.pack("<d", math.sin(t)))
            cloud_events(t, int(t))
            for station in uns:
                station.slow(pub, t)
            pub(f"{UNS_SITE}/energy/power", f"{850 + 120 * math.sin(t / 30) + random.gauss(0, 5):.1f}")
            pub(f"{UNS_SITE}/energy/total", f"{12_500_000 + t * 0.24:.1f}")
            if int(t) % 5 == 0:
                for station in uns:
                    station.oee(pub, t)
                for line in range(1, UNS_LINES + 1):
                    uns_line_order(pub, line, t)
                    stations = [s for s in uns if f"/line{line}/" in s.base + "/"]
                    pub(f"{UNS_AREA}/line{line}/kpi/output", json.dumps({
                        "good": sum(s.good for s in stations), "bad": sum(s.bad for s in stations),
                        "running": sum(s.state == "Running" for s in stations), "stations": len(stations),
                    }))
            if REBIRTH_S > 0 and int(t) > 0 and int(t) % int(REBIRTH_S) == 0:
                send("NBIRTH", edge1, None, node_metrics)
                send("DBIRTH", edge1, "Press1", devices["Press1"].birth(t))
                if press2_dead_since is None:
                    send("DBIRTH", edge1, "Press2", devices["Press2"].birth(t))
                send("NBIRTH", edge2, None, edge2_birth)
            send("NDATA", edge2, None, [(None, 10, INT64, int(t)), (None, 11, DOUBLE, 21.5 + math.sin(t / 10))])
            send("DDATA", edge1, "Press1", devices["Press1"].data(t, ["Pressure", "Running", "Count", "State", "Motor/Current"]))
            if press2_dead_since is None:
                send("DDATA", edge1, "Press2", devices["Press2"].data(t, ["Pressure", "Running", "Count", "State", "Motor/Current"]))

            # Press2 goes offline for 5 s every DEATH_PERIOD_S, so device state and Bad metrics can be seen.
            if press2_dead_since is None and DEATH_PERIOD_S > 0 and int(t) > 0 and int(t) % int(DEATH_PERIOD_S) == 0:
                send("DDEATH", edge1, "Press2", [])
                press2_dead_since = t
            elif press2_dead_since is not None and t - press2_dead_since >= 5:
                send("DBIRTH", edge1, "Press2", devices["Press2"].birth(t))
                press2_dead_since = None

        tick += 1
        delay = start + tick * tick_s - time.time()
        if delay > 0:
            time.sleep(delay)
        else:
            tick = int((time.time() - start) / tick_s) + 1


if __name__ == "__main__":
    main()
