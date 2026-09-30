"""OPC UA server with custom structured types and a large address space (asyncua).

Complements the opc-plc simulator with what it does not cover:
  - custom DataTypes with DataTypeDefinition: enums, an OptionSet, nested structures, structure arrays,
    optional fields and a union, plus a custom ObjectType with typed instances
  - multi-dimensional arrays and every built-in scalar type
  - values changing every 10 / 20 / 50 ms (structures included)
  - a large address space: a deep tree and one flat folder with thousands of children (browse continuation points)

Environment:
  OPCUA_CUSTOM_PORT         TCP port (default 4841)
  OPCUA_CUSTOM_HOST         host name advertised in the endpoint URL (default localhost)
  OPCUA_CUSTOM_FAST_MS      base tick in ms (default 10; 0 disables value changes)
  OPCUA_CUSTOM_LARGE        "areas,lines,tags" of the deep Large tree (default 10,10,50 = 5000 variables)
  OPCUA_CUSTOM_FLAT         number of variables in the single Flat folder (default 10000)
"""

import asyncio
import datetime
import logging
import math
import os
import random
import time

from asyncua import Server, ua
from asyncua.common.structures104 import new_enum, new_struct, new_struct_field

NAMESPACE = "urn:opcuabrowser:simulator:custom"
log = logging.getLogger("opcua-custom")


async def create_types(server, idx):
    state_enum = await new_enum(server, idx, "MachineState", ["Idle", "Running", "Paused", "Faulted", "Maintenance"])
    options = await new_enum(server, idx, "AlarmFlags", ["Overheat", "LowPressure", "DoorOpen", "EStop"], option_set=True)
    vector, _ = await new_struct(server, idx, "Vector3", [
        new_struct_field("X", ua.VariantType.Double),
        new_struct_field("Y", ua.VariantType.Double),
        new_struct_field("Z", ua.VariantType.Double),
    ])
    tool, _ = await new_struct(server, idx, "ToolInfo", [
        new_struct_field("Id", ua.VariantType.String),
        new_struct_field("Shots", ua.VariantType.UInt32),
        new_struct_field("Installed", ua.VariantType.DateTime),
        new_struct_field("Offset", vector),
    ])
    status, _ = await new_struct(server, idx, "MachineStatus", [
        new_struct_field("State", state_enum),
        new_struct_field("Speed", ua.VariantType.Double),
        new_struct_field("Position", vector),
        new_struct_field("Tool", tool),
        new_struct_field("Alarms", ua.VariantType.Int32, array=True),
        new_struct_field("Messages", ua.VariantType.String, array=True),
        new_struct_field("Waypoints", vector, array=True),
        new_struct_field("Ok", ua.VariantType.Boolean),
    ])
    optional, _ = await new_struct(server, idx, "QualityRecord", [
        new_struct_field("PartId", ua.VariantType.String),
        new_struct_field("Weight", ua.VariantType.Float),
        new_struct_field("Comment", ua.VariantType.String, optional=True),
        new_struct_field("Measurement", vector, optional=True),
    ])
    union, _ = await new_struct(server, idx, "SetpointValue", [
        new_struct_field("Numeric", ua.VariantType.Double),
        new_struct_field("Text", ua.VariantType.String),
        new_struct_field("Flag", ua.VariantType.Boolean),
    ], is_union=True)
    return {"MachineState": state_enum, "AlarmFlags": options, "Vector3": vector, "ToolInfo": tool,
            "MachineStatus": status, "QualityRecord": optional, "SetpointValue": union}


async def create_machine_type(server, idx, types):
    machine_type = await server.nodes.base_object_type.add_object_type(idx, "MachineType")
    for name, dtype, value in [("SerialNumber", ua.NodeId(ua.ObjectIds.String), "SN-000"),
                               ("Speed", ua.NodeId(ua.ObjectIds.Double), 0.0)]:
        var = await machine_type.add_variable(idx, name, ua.Variant(value), datatype=dtype)
        await var.set_modelling_rule(True)
    status = await machine_type.add_variable(idx, "Status", ua.Variant(None, ua.VariantType.ExtensionObject), datatype=types["MachineStatus"].nodeid)
    await status.set_modelling_rule(True)
    state = await machine_type.add_variable(idx, "State", ua.Variant(0, ua.VariantType.Int32), datatype=types["MachineState"].nodeid)
    await state.set_modelling_rule(True)
    return machine_type


def make_status(ua_types, t, i):
    V, T, S = ua_types["Vector3"], ua_types["ToolInfo"], ua_types["MachineStatus"]
    phase = t * 2.0 + i
    return S(
        State=ua_types["MachineState"](int(t / 5 + i) % 5),
        Speed=1500.0 + 100.0 * math.sin(phase),
        Position=V(X=math.sin(phase), Y=math.cos(phase), Z=0.1 * i),
        Tool=T(Id="T-{:04d}".format(42 + i), Shots=int(t * 10) + i, Installed=datetime.datetime(2026, 1, 1 + i, tzinfo=datetime.timezone.utc),
               Offset=V(X=0.01 * i, Y=0.0, Z=-0.02)),
        Alarms=[0, 101 * i, 0] if i else [],
        Messages=["ok", "cycle {}".format(int(t * 10))],
        Waypoints=[V(X=j, Y=j * 2.0, Z=math.sin(phase + j)) for j in range(3)],
        Ok=int(t * 10 + i) % 7 != 0,
    )


async def build(server, idx, types):
    ua_types = {name: getattr(ua, name) for name in types}
    objects = server.nodes.objects
    root = await objects.add_folder(idx, "Custom")
    animated = []  # (node, period_ms, fn(t) -> Variant)

    # Machines: typed instances of MachineType with structured values.
    machine_type = await create_machine_type(server, idx, types)
    machines = await root.add_folder(idx, "Machines")
    for i in range(1, 4):
        machine = await machines.add_object(idx, "Machine{}".format(i), objecttype=machine_type.nodeid)
        await (await machine.get_child("{}:SerialNumber".format(idx))).write_value("SN-{:03d}".format(i))
        status = await machine.get_child("{}:Status".format(idx))
        speed = await machine.get_child("{}:Speed".format(idx))
        state = await machine.get_child("{}:State".format(idx))
        animated.append((status, 50, lambda t, i=i: ua.Variant(make_status(ua_types, t, i), ua.VariantType.ExtensionObject)))
        animated.append((speed, 10, lambda t, i=i: ua.Variant(1500.0 + 100.0 * math.sin(t * 2.0 + i), ua.VariantType.Double)))
        animated.append((state, 1000, lambda t, i=i: ua.Variant(int(t / 5 + i) % 5, ua.VariantType.Int32)))

    # Structured values that are not part of an object type.
    structs = await root.add_folder(idx, "Structures")
    V = ua_types["Vector3"]
    position = await structs.add_variable(idx, "Position", ua.Variant(V(X=1.0, Y=2.0, Z=3.0), ua.VariantType.ExtensionObject), datatype=types["Vector3"].nodeid)
    animated.append((position, 10, lambda t: ua.Variant(V(X=math.sin(t), Y=math.cos(t), Z=t % 10), ua.VariantType.ExtensionObject)))
    history = await structs.add_variable(idx, "StatusHistory", ua.Variant([make_status(ua_types, 0, j) for j in range(5)], ua.VariantType.ExtensionObject),
                                         datatype=types["MachineStatus"].nodeid)
    await history.write_value_rank(1)
    animated.append((history, 1000, lambda t: ua.Variant([make_status(ua_types, t - j, j) for j in range(5)], ua.VariantType.ExtensionObject)))
    Q = ua_types["QualityRecord"]
    full = Q(PartId="P-1", Weight=1.25, Comment="measured", Measurement=V(X=1.0, Y=1.0, Z=1.0))
    await structs.add_variable(idx, "QualityFull", ua.Variant(full, ua.VariantType.ExtensionObject), datatype=types["QualityRecord"].nodeid)
    await structs.add_variable(idx, "QualityPartial", ua.Variant(Q(PartId="P-2", Weight=1.3), ua.VariantType.ExtensionObject), datatype=types["QualityRecord"].nodeid)
    U = ua_types["SetpointValue"]
    numeric, text = U(), U()
    numeric.Numeric, text.Text = 42.5, "AUTO"  # union members are properties that set the switch field
    for name, value in [("SetpointNumeric", numeric), ("SetpointText", text)]:
        await structs.add_variable(idx, name, ua.Variant(value, ua.VariantType.ExtensionObject), datatype=types["SetpointValue"].nodeid)
    state = await structs.add_variable(idx, "State", ua.Variant(1, ua.VariantType.Int32), datatype=types["MachineState"].nodeid)
    animated.append((state, 1000, lambda t: ua.Variant(int(t) % 5, ua.VariantType.Int32)))
    await structs.add_variable(idx, "Alarms", ua.Variant(0b0101, ua.VariantType.UInt32), datatype=types["AlarmFlags"].nodeid)

    # Every built-in scalar, arrays and multi-dimensional arrays.
    data_types = await root.add_folder(idx, "DataTypes")
    now = datetime.datetime.now(datetime.timezone.utc)
    scalars = [("Boolean", True, ua.VariantType.Boolean), ("SByte", -8, ua.VariantType.SByte), ("Byte", 200, ua.VariantType.Byte),
               ("Int16", -1234, ua.VariantType.Int16), ("UInt16", 60000, ua.VariantType.UInt16), ("Int32", -123456, ua.VariantType.Int32),
               ("UInt32", 4000000000, ua.VariantType.UInt32), ("Int64", -(2 ** 60), ua.VariantType.Int64), ("UInt64", 2 ** 64 - 1, ua.VariantType.UInt64),
               ("Float", 3.14159, ua.VariantType.Float), ("Double", 2.718281828459045, ua.VariantType.Double), ("String", "Hello OPC UA", ua.VariantType.String),
               ("DateTime", now, ua.VariantType.DateTime), ("Guid", ua.uuid.UUID("72962b91-fa75-4ae6-8d28-b404dc7daf63"), ua.VariantType.Guid),
               ("ByteString", bytes(range(16)), ua.VariantType.ByteString), ("LocalizedText", ua.LocalizedText("Hallo", "de-DE"), ua.VariantType.LocalizedText),
               ("QualifiedName", ua.QualifiedName("Name", idx), ua.VariantType.QualifiedName), ("NodeId", ua.NodeId(85, 0), ua.VariantType.NodeId),
               ("StatusCode", ua.StatusCode(ua.StatusCodes.BadOutOfRange), ua.VariantType.StatusCode), ("XmlElement", ua.XmlElement("<a>1</a>"), ua.VariantType.XmlElement)]
    for name, value, vtype in scalars:
        var = await data_types.add_variable(idx, name, ua.Variant(value, vtype))
        await var.set_writable()
    arrays = await data_types.add_folder(idx, "Arrays")
    for name, values, vtype in [("Int32Array", list(range(10)), ua.VariantType.Int32), ("DoubleArray", [i / 3 for i in range(10)], ua.VariantType.Double),
                                ("StringArray", ["alpha", "beta", "gamma"], ua.VariantType.String), ("BooleanArray", [True, False] * 4, ua.VariantType.Boolean),
                                ("EmptyArray", [], ua.VariantType.Int32), ("LargeArray", list(range(10000)), ua.VariantType.Int32)]:
        var = await arrays.add_variable(idx, name, ua.Variant(values, vtype))
        await var.write_value_rank(1)
    matrix = await arrays.add_variable(idx, "Matrix3x4", ua.Variant([[r * 4 + c for c in range(4)] for r in range(3)], ua.VariantType.Int32, Dimensions=[3, 4]))
    await matrix.write_value_rank(2)
    await matrix.write_array_dimensions([3, 4])
    cube = await arrays.add_variable(idx, "Cube2x3x4", ua.Variant([[[float(a * 12 + b * 4 + c) for c in range(4)] for b in range(3)] for a in range(2)], ua.VariantType.Double, Dimensions=[2, 3, 4]))
    await cube.write_value_rank(3)
    animated.append((matrix, 20, lambda t: ua.Variant([[int(t * 50) + r * 4 + c for c in range(4)] for r in range(3)], ua.VariantType.Int32, Dimensions=[3, 4])))

    # Plain fast-changing values at 10 / 20 / 50 ms.
    fast = await root.add_folder(idx, "Fast")
    for period in (10, 20, 50):
        group = await fast.add_folder(idx, "Every{}ms".format(period))
        counter = await group.add_variable(idx, "Counter", ua.Variant(0, ua.VariantType.UInt32))
        animated.append((counter, period, lambda t, p=period: ua.Variant(int(t * 1000 / p) & 0xFFFFFFFF, ua.VariantType.UInt32)))
        sine = await group.add_variable(idx, "Sine", ua.Variant(0.0, ua.VariantType.Double))
        animated.append((sine, period, lambda t: ua.Variant(math.sin(t * 2 * math.pi), ua.VariantType.Double)))
        toggle = await group.add_variable(idx, "Toggle", ua.Variant(False, ua.VariantType.Boolean))
        animated.append((toggle, period, lambda t, p=period: ua.Variant(int(t * 1000 / p) % 2 == 0, ua.VariantType.Boolean)))
        noise = await group.add_variable(idx, "Noise", ua.Variant(0.0, ua.VariantType.Float))
        animated.append((noise, period, lambda t: ua.Variant(random.gauss(0, 1), ua.VariantType.Float)))
        stamp = await group.add_variable(idx, "Timestamp", ua.Variant(now, ua.VariantType.DateTime))
        animated.append((stamp, period, lambda t: ua.Variant(datetime.datetime.now(datetime.timezone.utc), ua.VariantType.DateTime)))

    # Edge cases for a browser.
    edge = await root.add_folder(idx, "EdgeCases")
    await edge.add_variable(ua.NodeId("Special chars: äöü ÆØÅ / \\ \" ' <&> 日本語", idx), "Special äöü 日本語 / \\ <&>", 1)
    await edge.add_variable(ua.NodeId("L" + "o" * 3000 + "ng", idx), "LongNodeId", 1)
    await edge.add_variable(ua.NodeId(bytes(range(16)), idx, ua.NodeIdType.ByteString), "OpaqueNodeId", 1)
    await edge.add_variable(ua.NodeId(ua.uuid.UUID("0d4a5e0f-1a2b-4c3d-8e9f-001122334455"), idx), "GuidNodeId", 1)
    await edge.add_variable(idx, "NullValue", ua.Variant(None, ua.VariantType.Null))
    for name, value, code in [("BadStatus", ua.Variant(0, ua.VariantType.Int32), ua.StatusCodes.BadSensorFailure),
                              ("UncertainStatus", ua.Variant(1.5, ua.VariantType.Double), ua.StatusCodes.UncertainLastUsableValue)]:
        var = await edge.add_variable(idx, name, value)
        await server.write_attribute_value(var.nodeid, ua.DataValue(value, StatusCode=ua.StatusCode(code)))
    await edge.add_variable(idx, "LongString", "x" * 100000)
    await edge.add_variable(idx, "EmptyString", "")
    no_access = await edge.add_variable(idx, "NotReadable", 7)
    await no_access.write_attribute(ua.AttributeIds.AccessLevel, ua.DataValue(ua.Variant(0, ua.VariantType.Byte)))
    await no_access.write_attribute(ua.AttributeIds.UserAccessLevel, ua.DataValue(ua.Variant(0, ua.VariantType.Byte)))
    deep = edge
    for level in range(1, 31):
        deep = await deep.add_folder(idx, "Level{:02d}".format(level))
    await deep.add_variable(idx, "Bottom", "30 levels down")
    await edge.add_method(idx, "Reset", lambda parent: [], [], [])

    # Large address space.
    areas, lines, tags = (int(x) for x in os.environ.get("OPCUA_CUSTOM_LARGE", "10,10,50").split(","))
    large = await root.add_folder(idx, "Large")
    kinds = [(0.0, ua.VariantType.Double), (0, ua.VariantType.Int32), (False, ua.VariantType.Boolean), ("", ua.VariantType.String)]
    for a in range(1, areas + 1):
        area = await large.add_folder(idx, "Area{:02d}".format(a))
        for l in range(1, lines + 1):
            line = await area.add_folder(idx, "Line{:02d}".format(l))
            for n in range(1, tags + 1):
                value, vtype = kinds[n % len(kinds)]
                await line.add_variable(ua.NodeId("Large.A{:02d}.L{:02d}.T{:03d}".format(a, l, n), idx), "Tag{:03d}".format(n), ua.Variant(value, vtype))
    flat_count = int(os.environ.get("OPCUA_CUSTOM_FLAT", "10000"))
    flat = await root.add_folder(idx, "Flat")
    for n in range(flat_count):
        await flat.add_variable(ua.NodeId("Flat.{:05d}".format(n), idx), "Item{:05d}".format(n), ua.Variant(n, ua.VariantType.Int32))
    log.info("Large tree: %d variables, Flat: %d variables", areas * lines * tags, flat_count)
    return animated


async def animate(server, animated, tick_ms):
    tick_s = tick_ms / 1000.0
    schedule = [(node.nodeid, max(1, round(period / tick_ms)), fn) for node, period, fn in animated]
    print("Animating {} nodes, tick {} ms".format(len(schedule), tick_ms), flush=True)
    start, tick = time.monotonic(), 0
    while True:
        elapsed = time.monotonic() - start
        for nodeid, every, fn in schedule:
            if tick % every == 0:
                try:
                    await server.write_attribute_value(nodeid, ua.DataValue(fn(elapsed), SourceTimestamp=datetime.datetime.now(datetime.timezone.utc)))
                except Exception:
                    log.exception("Update failed for %s", nodeid)
        tick += 1
        delay = start + tick * tick_s - time.monotonic()
        if delay < 0:
            tick = int((time.monotonic() - start) / tick_s) + 1
            delay = start + tick * tick_s - time.monotonic()
        await asyncio.sleep(max(delay, 0))


async def main():
    logging.basicConfig(level=logging.WARNING, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    log.setLevel(logging.INFO)
    port = int(os.environ.get("OPCUA_CUSTOM_PORT", "4841"))
    host = os.environ.get("OPCUA_CUSTOM_HOST", "localhost")
    server = Server()
    await server.init()
    server.set_endpoint("opc.tcp://0.0.0.0:{}/".format(port))
    server.set_server_name("OpcUaBrowser Custom Types Simulator")
    server.set_security_policy([ua.SecurityPolicyType.NoSecurity])
    idx = await server.register_namespace(NAMESPACE)
    types = await create_types(server, idx)
    await server.load_data_type_definitions()
    animated = await build(server, idx, types)
    async with server:
        # Advertise a host clients can reach instead of the 0.0.0.0 bind address.
        for endpoint in await server.get_endpoints():
            endpoint.EndpointUrl = "opc.tcp://{}:{}/".format(host, port)
        print("OPC UA custom server ready on opc.tcp://{}:{}/".format(host, port), flush=True)
        tick_ms = int(os.environ.get("OPCUA_CUSTOM_FAST_MS", "10"))
        if tick_ms > 0:
            await animate(server, animated, tick_ms)
        else:
            await asyncio.Event().wait()


if __name__ == "__main__":
    asyncio.run(main())
