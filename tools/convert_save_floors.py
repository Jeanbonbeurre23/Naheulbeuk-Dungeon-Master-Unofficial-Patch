#!/usr/bin/env python3
"""Converts a seven-floor NDM save to one with N copies of floor 4 inserted above floor 4.

NDM Unofficial Patch, docs/features/floor-conversion.md. The source save is only read. The result is written under a new
save name, with a marker beside it in NDMUnofficialPatch-data that tells the plugin (0.23.0 or later) to load it with N
inserted floors and to build those floors on the first load.

What the script changes, in the save's ECS dump (GameSave.MDDSaveData, Odin binary, gzip):
  * every floor number of 5 or above becomes floor + N (floors 5 and 6, the tavern and the roof, become 5+N and 6+N),
    in the fields listed in FLOOR_INT_FIELDS, in path requests and actions (any field named FloorId), and in the
    behaviour trees' blackboards (variable "Floor", and the floor number held in the y of their Vector3 variables);
  * every world height of 99.5 or above (floors 5 and 6 stand at 100 and 120) is raised by N x 20, in the float3
    fields listed in HEIGHT_PATHS and in the behaviour trees' float3 variables;
  * the grid identifiers of the grid manager's tile map carry the floor in their upper bits (floor << 26), and those
    of floors 5 and 6 are renumbered;
  * the per-floor maps (floor manager, wall manager, worker manager) are re-keyed, and get an entry for each new floor;
  * each new floor gets a floor data entity (as FloorDataUtility.CreateFloorEntity makes) and an empty wall map entity
    (as Walls.UpdateWallMapSystem.Init makes), both copied from floor 4's and appended to the world's entities.
The floors' rooms, tiles, walls and doors are not written here: the plugin builds them with the game's own functions on
the first load (FloorConversion.cs).

Every change is listed in a report, and the result is read back and checked before it is written.
"""
import argparse
import collections
import datetime
import os
import struct
import sys
import zlib

# ---- Odin binary format (Sirenix.Serialization BinaryDataWriter) ----------------------------------------------------

NAMED = {1, 3, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27, 29, 31, 33, 35, 37, 39, 41, 43, 45, 50}
SIZE = {15: 1, 16: 1, 17: 1, 18: 1, 19: 2, 20: 2, 21: 2, 22: 2, 23: 4, 24: 4, 25: 4, 26: 4, 27: 8, 28: 8, 29: 8, 30: 8,
        31: 4, 32: 4, 33: 8, 34: 8, 35: 16, 36: 16, 37: 2, 38: 2, 41: 16, 42: 16, 43: 1, 44: 1, 45: 0, 46: 0,
        9: 4, 10: 4, 11: 4, 12: 4, 13: 16, 14: 16}
START = (1, 2, 3, 4, 6)
END = (5, 7)
INT_NAMED, INT_UNNAMED, FLOAT_NAMED, FLOAT_UNNAMED, SHORT_UNNAMED = 23, 24, 31, 32, 20


def rstr(b, p):
    flag = b[p]
    n = struct.unpack_from('<i', b, p + 1)[0]
    p += 5
    if flag == 1:
        return b[p:p + 2 * n].decode('utf-16le'), p + 2 * n
    return b[p:p + n].decode('latin-1'), p + n


def wstr(s):
    e = s.encode('utf-16le')
    return b'\x01' + struct.pack('<i', len(s)) + e


def tokens(b, p, end, types):
    """Yields (kind, name, pos, vpos, extra, next). vpos is where the value starts, next where the next token starts."""
    while p < end:
        k = b[p]
        start = p
        p += 1
        name = None
        if k in NAMED:
            name, p = rstr(b, p)
        if k in (1, 2, 3, 4):
            t = b[p]
            p += 1
            if t == 47:
                tid = struct.unpack_from('<i', b, p)[0]
                p += 4
                tn, p = rstr(b, p)
                types[tid] = tn
            elif t == 48:
                tid = struct.unpack_from('<i', b, p)[0]
                p += 4
                tn = types.get(tid, '?%d' % tid)
            elif t == 46:
                tn = None
            else:
                raise ValueError('bad type entry %d at %d' % (t, p - 1))
            nid = None
            if k in (1, 2):
                nid = struct.unpack_from('<i', b, p)[0]
                p += 4
            yield k, name, start, p, (tn, nid), p
        elif k in (5, 7, 49):
            yield k, name, start, p, None, p
        elif k == 6:
            n = struct.unpack_from('<q', b, p)[0]
            yield k, name, start, p, n, p + 8
            p += 8
        elif k == 8:
            n, es = struct.unpack_from('<ii', b, p)
            yield k, name, start, p, (n, es), p + 8 + n * es
            p += 8 + n * es
        elif k in (39, 40, 50, 51):
            s, q = rstr(b, p)
            yield k, name, start, p, s, q
            p = q
        elif k in SIZE:
            yield k, name, start, p, None, p + SIZE[k]
            p += SIZE[k]
        else:
            raise ValueError('unknown Odin entry %d at %d' % (k, start))


# ---- Save container ------------------------------------------------------------------------------------------------
# [int64 n1][Odin: GameFormatter+Format][int64 n2][Odin: GameSave.GameHeader][gzip: int64 length + Odin MDDSaveData]

def read_save(path):
    raw = open(path, 'rb').read()
    n1 = struct.unpack_from('<q', raw, 0)[0]
    fmt = raw[8:8 + n1]
    p = 8 + n1
    n2 = struct.unpack_from('<q', raw, p)[0]
    hdr = raw[p + 8:p + 8 + n2]
    gz = raw[p + 8 + n2:]
    d = zlib.decompressobj(16 + 15)
    body = d.decompress(gz)
    if not d.eof or d.unused_data:
        raise ValueError('the body of the save is not a single gzip stream')
    if struct.unpack_from('<q', body, 0)[0] != len(body) - 8:
        raise ValueError('the body length prefix does not match')
    return fmt, hdr, body


def gzip_bytes(data):
    c = zlib.compressobj(6, zlib.DEFLATED, -15)
    deflated = c.compress(data) + c.flush()
    # Same header as the game's (no name, no time, OS byte 0x0a).
    return b'\x1f\x8b\x08\x00\x00\x00\x00\x00\x00\x0a' + deflated + struct.pack('<II', zlib.crc32(data) & 0xffffffff, len(data) & 0xffffffff)


def write_save(path, fmt, hdr, body):
    out = struct.pack('<q', len(fmt)) + fmt + struct.pack('<q', len(hdr)) + hdr + gzip_bytes(body)
    with open(path, 'xb') as f:  # never overwrites
        f.write(out)


# ---- Index of the body ---------------------------------------------------------------------------------------------

def component_of(type_name):
    return type_name.split('[[')[1].split(',')[0] if type_name and '[[' in type_name else type_name


def index_body(b):
    """The pools (start, end, component) of MDDSaveData.PoolsData, where the world table starts, and the types."""
    types = {}
    depth = 0
    pools = []
    cur = None
    arr = None
    after = None
    for k, name, pos, vp, ex, nxt in tokens(b, 8, len(b), types):
        if k in START:
            if k == 6 and arr is None and depth == 2:
                arr = depth + 1
            if arr is not None and arr > 0 and depth == arr and k in (1, 2, 3, 4):
                cur = (pos, ex[0])
            depth += 1
        elif k in END:
            depth -= 1
            if arr is not None and arr > 0 and depth == arr and cur and k == 5:
                pools.append((cur[0], nxt, component_of(cur[1])))
                cur = None
            if k == 7 and arr is not None and arr > 0 and depth == arr - 1:
                after = nxt
                arr = -1
    return pools, after, types


class Node:
    __slots__ = ('k', 'name', 'pos', 'vp', 'ex', 'end', 'close', 'kids', 'value')

    def __init__(self, k, name, pos, vp, ex, end=None):
        self.k, self.name, self.pos, self.vp, self.ex, self.end = k, name, pos, vp, ex, end
        self.close = None  # for a node: where its closing token starts
        self.kids = []
        self.value = None

    def child(self, name):
        for c in self.kids:
            if c.name == name:
                return c
        raise KeyError(name)


VAL = {23: '<i', 24: '<i', 31: '<f', 32: '<f', 19: '<h', 20: '<h', 43: '<?', 44: '<?', 27: '<q', 28: '<q'}


def parse(b, start, end, types):
    """A tree of the tokens from start to end. A node's end is where its closing token ends."""
    root = Node(0, None, start, start, None)
    stack = [root]
    for k, name, pos, vp, ex, nxt in tokens(b, start, end, types):
        if k in START:
            n = Node(k, name, pos, vp, ex)
            stack[-1].kids.append(n)
            stack.append(n)
        elif k in END:
            n = stack.pop()
            n.end = nxt
            n.close = pos
        else:
            n = Node(k, name, pos, vp, ex, nxt)
            if k in VAL:
                n.value = struct.unpack_from(VAL[k], b, vp)[0]
            else:
                n.value = ex
            stack[-1].kids.append(n)
    return root.kids



# ---- The conversion ------------------------------------------------------------------------------------------------

FIRST_MOVED = 5          # floors from this number up move
HEIGHT = 20.0            # floor height in world units
MOVED_FROM_Y = FIRST_MOVED * HEIGHT - 0.5
COPIED = 4               # the floor the new floors copy

# Named int fields holding a floor number, by component.
FLOOR_INT_FIELDS = {
    'GridFloorComponent': {'DenseEntity/Floor'},
    'FloorDataComponent': {'DenseEntity/Floor'},
    'Walls.FloorWallMapComponent': {'DenseEntity/Floor'},
    'FloorTreeComponent': {'DenseEntity/FloorNumber'},
    'UnlockFloorConditionComponent': {'DenseEntity/FloorId'},
    'UnlockFloorNotificationComponent': {'DenseEntity/Floor'},
    'CameraManagerComponent': {'DenseEntity/CurrentFloor'},
    'Combat.CombatPositionsReservationComponent': {'DenseEntity/Floor'},
    'RelicComponent': {'DenseEntity/FloorExposed'},
    'FloorsManagerComponent': {'DenseEntity/MaxFloor'},
}
# Named float3 fields holding a world position (their y is raised), by component. '#' stands for an array element.
HEIGHT_PATHS = {
    'TransformComponent': {'DenseEntity/Position'},
    'ActionRootMotionDataComponent': {'DenseEntity/StartLoopPosition'},
    'FloorDataComponent': {'DenseEntity/Origin'},
    'CameraManagerComponent': {'DenseEntity/Target'},
    'GolbarghInteractionComponent': {'DenseEntity/ReferencePosition'},
    'MoveToPathActionDataComponent': {
        'DenseEntity/FloorSpline/m_nodes/#/Position', 'DenseEntity/FloorSpline/m_nodes/#/Tangent',
        'DenseEntity/FloorSpline/m_curves/#/m_node1/Position', 'DenseEntity/FloorSpline/m_curves/#/m_node1/Tangent',
        'DenseEntity/FloorSpline/m_curves/#/m_node2/Position', 'DenseEntity/FloorSpline/m_curves/#/m_node2/Tangent',
        'DenseEntity/FloorSpline/m_samples/#/Location'},
}
# Per-floor maps keyed by floor number: (component, field) -> value kind ('i' entity id or count, 'f' float).
FLOOR_MAPS = {
    ('FloorsManagerComponent', 'FloorEntityByID'): 'i',
    ('Walls.WallManagerComponent', 'FloorToMap'): 'i',
    ('WorkerManagerComponent', 'WorkersToSpawnByFloor'): 'i',
    ('WorkerManagerComponent', 'CurrentTimerByFloor'): 'f',
    ('WorkerManagerComponent', 'MaximumSpawnTimerByFloor'): 'f',
}


class Converter:
    def __init__(self, body, n, log):
        self.b = body
        self.n = n
        self.log = log
        self.pools, self.after, self.types = index_body(body)
        self.edits = []      # (pos, length replaced, new bytes)
        self.counts = collections.Counter()
        self.warnings = []
        self.by_comp = collections.defaultdict(list)
        for i, (s, e, c) in enumerate(self.pools):
            self.by_comp[c].append(i)

    # -- edits --
    def put(self, pos, fmt, value, what):
        self.edits.append((pos, struct.calcsize(fmt), struct.pack(fmt, value)))
        self.counts[what] += 1

    def insert(self, pos, data, what):
        self.edits.append((pos, 0, data))
        self.counts[what] += 1

    def moved(self, floor):
        return floor + self.n if floor >= FIRST_MOVED else floor

    def pool_tree(self, comp):
        idx = self.by_comp.get(comp, [])
        if len(idx) != 1:
            raise ValueError('expected one pool of %s, found %d' % (comp, len(idx)))
        s, e, _ = self.pools[idx[0]]
        return parse(self.b, s, e, dict(self.types))[0]

    # -- streaming pass over every pool --
    def stream(self):
        b = self.b
        for (s, e, comp) in self.pools:
            path = []        # names of the open nodes below the pool's entry, '#' for unnamed
            typ = []         # type name of each open node
            kv_key = [None]  # the last dictionary key read ($k) in a blackboard
            vec = []         # unnamed floats read in the current Vector3 node: list of (vpos, value)
            floor_ints = FLOOR_INT_FIELDS.get(comp, ())
            heights = HEIGHT_PATHS.get(comp, ())
            is_grid = comp == 'GridManagerComponent'
            is_bt = comp == 'BehaviourTreeOwnerComponent'
            grid_seq = 0
            for k, name, pos, vp, ex, nxt in tokens(b, s, e, dict(self.types)):
                if k in START:
                    path.append(name if name is not None else '#')
                    typ.append(ex[0] if k != 6 else 'array')
                    if is_bt and k in (1, 2) and ex[0] and ex[0].startswith('UnityEngine.Vector3'):
                        vec = []
                    if is_grid and name == 'TileEntities':
                        grid_seq = 0
                    continue
                if k in END:
                    if is_bt and typ and typ[-1] and typ[-1].startswith('UnityEngine.Vector3') and len(vec) == 3:
                        yp, yv = vec[1]
                        if yv >= FIRST_MOVED and yv == int(yv) and yv <= 30:
                            self.put(yp, '<f', yv + self.n, 'blackboard Vector3 floor (y)')
                    if path:
                        path.pop()
                        typ.pop()
                    continue
                rel = '/'.join(path[4:] + [name if name is not None else '#'])  # below pool/list/array/entry
                if k == 39 and is_bt and name == '$k':
                    kv_key[0] = ex
                    continue
                if k == INT_NAMED:
                    v = struct.unpack_from('<i', b, vp)[0]
                    if rel in floor_ints or (name == 'FloorId'):
                        if v >= FIRST_MOVED:
                            self.put(vp, '<i', v + self.n, '%s.%s' % (comp, rel))
                    elif 'Floor' in name and comp not in ('FloorsManagerComponent',):
                        if v >= FIRST_MOVED:
                            self.warnings.append('unhandled floor-like field %s %s = %d' % (comp, rel, v))
                    continue
                if k == INT_UNNAMED:
                    v = struct.unpack_from('<i', b, vp)[0]
                    if is_grid and len(path) >= 2 and path[-1] == 'TileEntities':
                        grid_seq += 1
                        if grid_seq > 0 and grid_seq % 2 == 1:  # keys and values alternate, keys first
                            fl = (v >> 26) & 0x1f
                            if v >= 0 and fl >= FIRST_MOVED:
                                self.put(vp, '<i', v + (self.n << 26), 'GridManagerComponent.TileEntities key')
                    elif is_bt and path and path[-1] == '$v' and typ[-1] and typ[-1].startswith('System.Int32') and kv_key[0] == 'Floor':
                        if v >= FIRST_MOVED:
                            self.put(vp, '<i', v + self.n, 'blackboard Floor')
                    continue
                if k == FLOAT_NAMED and name == 'y':
                    t = typ[-1] if typ else None
                    if t and t.startswith('Unity.Mathematics.float3'):
                        v = struct.unpack_from('<f', b, vp)[0]
                        node_path = '/'.join(path[4:])
                        if node_path in heights or (is_bt and path[-1] == '$v'):
                            if v >= MOVED_FROM_Y:
                                self.put(vp, '<f', v + self.n * HEIGHT, '%s.%s.y' % (comp, node_path))
                        elif v >= MOVED_FROM_Y and not (comp == 'MoveToPathActionDataComponent' and node_path.endswith('m_samples/#/Tangent')):
                            self.warnings.append('unhandled float3 height %s %s y = %g' % (comp, node_path, v))
                    continue
                if k == FLOAT_UNNAMED and is_bt and typ and typ[-1] and typ[-1].startswith('UnityEngine.Vector3'):
                    vec.append((vp, struct.unpack_from('<f', b, vp)[0]))
                    continue

    # -- maps keyed by floor: re-key, add the new floors --
    def floor_map(self, comp, field, kind, new_values):
        """new_values: floor -> value for floors 5..4+n."""
        pool = self.pool_tree(comp)
        entries = pool.child('PoolEntities').kids[0].kids  # array of entries
        if len(entries) != 1:
            raise ValueError('%s: %d entities, expected one' % (comp, len(entries)))
        m = entries[0].child('DenseEntity').child(field)
        cap, cnt = m.child('Capacity'), m.child('Count')
        pairs = [c for c in m.kids if c.k in (INT_UNNAMED, FLOAT_UNNAMED)]
        if len(pairs) != 2 * cnt.value:
            raise ValueError('%s.%s: %d values for a count of %d' % (comp, field, len(pairs), cnt.value))
        keys = [pairs[i].value for i in range(0, len(pairs), 2)]
        if sorted(keys) != list(range(0, 7)):
            raise ValueError('%s.%s: keys %s, expected 0 to 6' % (comp, field, sorted(keys)))
        before = {pairs[i].value: pairs[i + 1].value for i in range(0, len(pairs), 2)}
        for i in range(0, len(pairs), 2):
            if pairs[i].value >= FIRST_MOVED:
                self.put(pairs[i].vp, '<i', pairs[i].value + self.n, '%s.%s key' % (comp, field))
        new_count = cnt.value + self.n
        self.put(cnt.vp, '<i', new_count, '%s.%s count' % (comp, field))
        if cap.value < new_count:
            self.put(cap.vp, '<i', new_count, '%s.%s capacity' % (comp, field))
        data = b''
        for f in range(FIRST_MOVED, FIRST_MOVED + self.n):
            data += struct.pack('<Bi', INT_UNNAMED, f)
            v = new_values[f]
            data += struct.pack('<Bi', INT_UNNAMED, v) if kind == 'i' else struct.pack('<Bf', FLOAT_UNNAMED, v)
        self.insert(m.close, data, '%s.%s new floors' % (comp, field))
        return before

    # -- pool entries appended: copies of floor 4's --
    def entry_of_floor(self, comp, floor):
        pool = self.pool_tree(comp)
        arr = pool.child('PoolEntities').kids[0]
        found = [(i, e) for i, e in enumerate(arr.kids) if e.child('DenseEntity').child('Floor').value == floor]
        if len(found) != 1:
            raise ValueError('%s: %d entities of floor %d' % (comp, len(found), floor))
        i, e = found[0]
        if i == 0:
            raise ValueError('%s: floor %d is the first entity of the pool, whose bytes define types; not copied' % (comp, floor))
        return arr, e

    def add_floor_data(self, new_ids):
        arr, e = self.entry_of_floor('FloorDataComponent', COPIED)
        d = e.child('DenseEntity')
        origin_y = d.child('Origin').child('y')
        data = b''
        for k, ent in enumerate(new_ids, start=1):
            raw = bytearray(self.b[e.pos:e.end])
            struct.pack_into('<i', raw, e.child('SparseItemsIndex').vp - e.pos, ent)
            struct.pack_into('<f', raw, origin_y.vp - e.pos, origin_y.value + k * HEIGHT)
            struct.pack_into('<i', raw, d.child('Floor').vp - e.pos, COPIED + k)
            data += bytes(raw)
        self.put(arr.vp, '<q', arr.ex + self.n, 'FloorDataComponent entity count')
        self.insert(arr.close, data, 'FloorDataComponent new floors')
        return origin_y.value

    def add_wall_maps(self, new_ids):
        arr, e = self.entry_of_floor('Walls.FloorWallMapComponent', COPIED)
        d = e.child('DenseEntity')
        maps = [d.child('WallMap'), d.child('WallFaceMap'), d.child('WallFaceVisualMap')]
        # Each map keeps IsCreated, Capacity and Count, loses its entries, and gets Count 0.
        skip = []
        for m in maps:
            cnt = m.child('Count')
            first_entry = [c for c in m.kids if c.pos >= cnt.end]
            if first_entry:
                skip.append((first_entry[0].pos, m.close))
        data = b''
        for k, ent in enumerate(new_ids, start=1):
            raw = bytearray()
            pos = e.pos
            for a, z in sorted(skip):
                raw += self.b[pos:a]
                pos = z
            raw += self.b[pos:e.end]
            # Offsets inside the trimmed copy: everything before the first skipped span keeps its offset.
            first_cut = min(a for a, z in skip) if skip else e.end
            for fld, val, fmt in (('SparseItemsIndex', ent, '<i'),):
                off = e.child(fld).vp - e.pos
                struct.pack_into(fmt, raw, off, val)
            fl = d.child('Floor')
            if fl.vp >= first_cut:
                raise ValueError('wall map floor field after the maps')
            struct.pack_into('<i', raw, fl.vp - e.pos, COPIED + k)
            # Counts to 0: each Count field's offset shifts by the bytes cut before it.
            for m in maps:
                cnt = m.child('Count')
                cut_before = sum(z - a for a, z in skip if z <= cnt.vp)
                struct.pack_into('<i', raw, cnt.vp - e.pos - cut_before, 0)
            data += bytes(raw)
        self.put(arr.vp, '<q', arr.ex + self.n, 'Walls.FloorWallMapComponent entity count')
        self.insert(arr.close, data, 'Walls.FloorWallMapComponent new floors')
        caps = [m.child('Capacity').value for m in maps]
        return caps

    def entity_floors(self):
        """GridFloorComponent.Floor of every entity, before the conversion."""
        idx = self.by_comp['GridFloorComponent']
        s, e, _ = self.pools[idx[0]]
        floors = {}
        entity = None
        for k, name, pos, vp, ex, nxt in tokens(self.b, s, e, dict(self.types)):
            if k == INT_NAMED and name == 'SparseItemsIndex':
                entity = struct.unpack_from('<i', self.b, vp)[0]
            elif k == INT_NAMED and name == 'Floor' and entity is not None:
                floors[entity] = struct.unpack_from('<i', self.b, vp)[0]
                entity = None
        return floors

    def unlink_stairs(self):
        """Drops the stair links that the renumbering stretches over more than one floor (floor 4 to the tavern floor).
        The game links a stair to those of the floors just above and below (StairsUtility.SetupLinks), and its
        pathfinding throws on a link to any other floor."""
        floors = self.entity_floors()
        pool = self.pool_tree('StairComponent')
        dropped = []
        for entry in pool.child('PoolEntities').kids[0].kids:
            ent = entry.child('SparseItemsIndex').value
            links = entry.child('DenseEntity').child('LinkedStairs')
            ints = [c for c in links.kids if c.k == INT_UNNAMED]
            mine = floors.get(ent)
            keep = []
            for c in ints:
                theirs = floors.get(c.value)
                if mine is not None and theirs is not None and abs(self.moved(mine) - self.moved(theirs)) == 1:
                    keep.append(c)
                else:
                    dropped.append('stair %d (floor %s -> %s) to %d (floor %s -> %s)' % (
                        ent, mine, None if mine is None else self.moved(mine), c.value, theirs, None if theirs is None else self.moved(theirs)))
                    self.edits.append((c.pos, c.end - c.pos, b''))
                    self.counts['StairComponent link dropped'] += 1
            if len(keep) != len(ints):
                self.put(links.child('Length').vp, '<i', len(keep), 'StairComponent link count')
        return dropped

    def world_table(self):
        world = parse(self.b, self.after + 1, len(self.b) - 1, dict(self.types))
        named = {n.name: n for n in world}
        gens = named['EntitiesGenArray'].kids[0]
        last = named['LastEntitiesIndex']
        used = named['EntitiesUseCount']
        cap = named['EntitiesCapacity']
        if gens.ex != last.value:
            raise ValueError('generation list of %d entries for %d entities' % (gens.ex, last.value))
        return gens, last, used, cap

    def convert(self):
        n = self.n
        gens, last, used, cap = self.world_table()
        first_new = last.value
        floor_data_ids = list(range(first_new, first_new + n))
        wall_map_ids = list(range(first_new + n, first_new + 2 * n))
        self.stream()
        stairs = self.unlink_stairs()
        origin_y = self.add_floor_data(floor_data_ids)
        caps = self.add_wall_maps(wall_map_ids)
        new_floors = range(FIRST_MOVED, FIRST_MOVED + n)
        self.floor_map('FloorsManagerComponent', 'FloorEntityByID', 'i', dict(zip(new_floors, floor_data_ids)))
        self.floor_map('Walls.WallManagerComponent', 'FloorToMap', 'i', dict(zip(new_floors, wall_map_ids)))
        workers = self.pool_tree('WorkerManagerComponent').child('PoolEntities').kids[0].kids[0].child('DenseEntity')
        def value_of(field, floor):
            m = workers.child(field)
            p = [c for c in m.kids if c.k in (INT_UNNAMED, FLOAT_UNNAMED)]
            return {p[i].value: p[i + 1].value for i in range(0, len(p), 2)}[floor]
        self.floor_map('WorkerManagerComponent', 'WorkersToSpawnByFloor', 'i', {f: 0 for f in new_floors})
        self.floor_map('WorkerManagerComponent', 'CurrentTimerByFloor', 'f', {f: 0.0 for f in new_floors})
        spawn4 = value_of('MaximumSpawnTimerByFloor', COPIED)
        self.floor_map('WorkerManagerComponent', 'MaximumSpawnTimerByFloor', 'f', {f: spawn4 for f in new_floors})
        # The new entities: generation 1, after the last entity.
        self.put(gens.vp, '<q', gens.ex + 2 * n, 'world: generation list length')
        self.insert(gens.close, struct.pack('<Bh', SHORT_UNNAMED, 1) * (2 * n), 'world: generations of the new entities')
        self.put(last.vp, '<i', last.value + 2 * n, 'world: LastEntitiesIndex')
        self.put(used.vp, '<i', used.value + 2 * n, 'world: EntitiesUseCount')
        if cap.value < last.value + 2 * n:
            raise ValueError('entity capacity %d too small for %d entities' % (cap.value, last.value + 2 * n))
        return dict(floor_data_ids=floor_data_ids, wall_map_ids=wall_map_ids, origin_y=origin_y, wall_map_caps=caps,
                    spawn_timer=spawn4, stairs=stairs)

    def apply(self):
        edits = sorted(self.edits, key=lambda x: (x[0], x[1]))
        out = bytearray()
        pos = 0
        last_end = -1
        for p, length, data in edits:
            if p < last_end:
                raise ValueError('overlapping edits at %d' % p)
            out += self.b[pos:p]
            out += data
            pos = p + length
            last_end = pos if length else p
        out += self.b[pos:]
        struct.pack_into('<q', out, 0, len(out) - 8)
        return bytes(out)


def rename_header(hdr, new_name):
    types = {}
    for k, name, pos, vp, ex, nxt in tokens(hdr, 0, len(hdr), types):
        if k == 39 and name == 'm_gameName':
            old = ex
            return hdr[:vp] + wstr(new_name) + hdr[nxt:], old
    raise ValueError('no m_gameName in the header')


# ---- Checks on the result ------------------------------------------------------------------------------------------

def survey(body):
    """Floor numbers, heights and per-floor maps of a body, for comparison before and after."""
    pools, after, types = index_body(body)
    floors = collections.Counter()
    heights = collections.Counter()
    data_floors = []
    wall_floors = []
    maps = {}
    gridkeys = collections.Counter()
    for (s, e, comp) in pools:
        path = []
        typ = []
        seq = 0
        for k, name, pos, vp, ex, nxt in tokens(body, s, e, dict(types)):
            if k in START:
                path.append(name if name is not None else '#')
                typ.append(ex[0] if k != 6 else 'array')
                if name == 'TileEntities':
                    seq = 0
                continue
            if k in END:
                if path:
                    path.pop()
                    typ.pop()
                continue
            if k == INT_NAMED and name == 'Floor' and comp == 'GridFloorComponent':
                floors[struct.unpack_from('<i', body, vp)[0]] += 1
            elif k == INT_NAMED and name == 'Floor' and comp == 'FloorDataComponent':
                data_floors.append(struct.unpack_from('<i', body, vp)[0])
            elif k == INT_NAMED and name == 'Floor' and comp == 'Walls.FloorWallMapComponent':
                wall_floors.append(struct.unpack_from('<i', body, vp)[0])
            elif k == FLOAT_NAMED and name == 'y' and comp == 'TransformComponent':
                y = struct.unpack_from('<f', body, vp)[0]
                heights[int(y // 20) if y == y and abs(y) < 1e6 else 'none'] += 1
            elif k == INT_UNNAMED and comp == 'GridManagerComponent' and path and path[-1] == 'TileEntities':
                seq += 1
                if seq % 2 == 1:
                    gridkeys[(struct.unpack_from('<i', body, vp)[0] >> 26) & 0x1f] += 1
    for comp, field in FLOOR_MAPS:
        idx = [i for i, p in enumerate(pools) if p[2] == comp][0]
        s, e, _ = pools[idx]
        tree = parse(body, s, e, dict(types))[0]
        m = tree.child('PoolEntities').kids[0].kids[0].child('DenseEntity').child(field)
        p = [c for c in m.kids if c.k in (INT_UNNAMED, FLOAT_UNNAMED)]
        maps[(comp, field)] = (m.child('Count').value, m.child('Capacity').value, {p[i].value: p[i + 1].value for i in range(0, len(p), 2)})
    world = {n.name: n for n in parse(body, after + 1, len(body) - 1, dict(types))}
    gens = [c.value for c in world['EntitiesGenArray'].kids[0].kids]
    return dict(floors=floors, heights=heights, data_floors=sorted(data_floors), wall_floors=sorted(wall_floors), maps=maps,
                gridkeys=gridkeys, last=world['LastEntitiesIndex'].value, used=world['EntitiesUseCount'].value,
                gens=len(gens), gens_tail=gens[-12:], pools=len(pools))


def stair_links(body):
    """(stair, its floor, linked stair, that one's floor) for every stair link of a body."""
    c = Converter.__new__(Converter)
    c.b = body
    c.pools, c.after, c.types = index_body(body)
    c.by_comp = collections.defaultdict(list)
    for i, (s, e, comp) in enumerate(c.pools):
        c.by_comp[comp].append(i)
    floors = c.entity_floors()
    out = []
    for entry in c.pool_tree('StairComponent').child('PoolEntities').kids[0].kids:
        ent = entry.child('SparseItemsIndex').value
        links = entry.child('DenseEntity').child('LinkedStairs')
        ints = [x.value for x in links.kids if x.k == INT_UNNAMED]
        if len(ints) != links.child('Length').value:
            out.append((ent, floors.get(ent), None, 'length %d for %d links' % (links.child('Length').value, len(ints))))
        for o in ints:
            out.append((ent, floors.get(ent), o, floors.get(o)))
    return out


def check(before, after, n, info, log):
    ok = True

    def fail(msg):
        nonlocal ok
        ok = False
        log('CHECK FAILED: ' + msg)

    moved = lambda f: f + n if f >= FIRST_MOVED else f
    exp = collections.Counter({moved(f): c for f, c in before['floors'].items()})
    if after['floors'] != exp:
        fail('tile and entity floors %s, expected %s' % (dict(after['floors']), dict(exp)))
    expected_data = sorted(moved(f) for f in before['data_floors']) + []
    expected_data = sorted(expected_data + list(range(FIRST_MOVED, FIRST_MOVED + n)))
    if after['data_floors'] != expected_data:
        fail('floor data floors %s, expected %s' % (after['data_floors'], expected_data))
    if after['wall_floors'] != expected_data:
        fail('wall map floors %s, expected %s' % (after['wall_floors'], expected_data))
    hb = collections.Counter()
    for band, c in before['heights'].items():
        hb[band + n if band != 'none' and band >= FIRST_MOVED else band] += c
    if after['heights'] != hb:
        fail('heights by floor band %s, expected %s' % (dict(after['heights']), dict(hb)))
    gk = collections.Counter({moved(f): c for f, c in before['gridkeys'].items()})
    if after['gridkeys'] != gk:
        fail('grid map keys by floor %s, expected %s' % (dict(after['gridkeys']), dict(gk)))
    for key, (cnt, cap, m) in after['maps'].items():
        bcnt, bcap, bm = before['maps'][key]
        if sorted(m) != list(range(0, 7 + n)) or cnt != 7 + n or cap < cnt:
            fail('%s.%s keys %s count %d capacity %d' % (key[0], key[1], sorted(m), cnt, cap))
        for f, v in bm.items():
            if m.get(moved(f)) != v:
                fail('%s.%s floor %d value %r, expected %r' % (key[0], key[1], moved(f), m.get(moved(f)), v))
    fm = after['maps'][('FloorsManagerComponent', 'FloorEntityByID')][2]
    for k, f in enumerate(range(FIRST_MOVED, FIRST_MOVED + n)):
        if fm[f] != info['floor_data_ids'][k]:
            fail('floor manager entry of floor %d is %d' % (f, fm[f]))
    wm = after['maps'][('Walls.WallManagerComponent', 'FloorToMap')][2]
    for k, f in enumerate(range(FIRST_MOVED, FIRST_MOVED + n)):
        if wm[f] != info['wall_map_ids'][k]:
            fail('wall manager entry of floor %d is %d' % (f, wm[f]))
    if after['last'] != before['last'] + 2 * n or after['used'] != before['used'] + 2 * n or after['gens'] != after['last']:
        fail('world: %d entities, %d alive, %d generations' % (after['last'], after['used'], after['gens']))
    if after['gens_tail'][-2 * n:] != [1] * (2 * n):
        fail('generations of the new entities %s' % after['gens_tail'])
    if after['pools'] != before['pools']:
        fail('%d pools after, %d before' % (after['pools'], before['pools']))
    return ok


# ---- Main ----------------------------------------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('source', help='the seven-floor save, e.g. .../NDM/Save/Game_Default_1.sav (only read)')
    ap.add_argument('name', help='the new save name shown in the game, e.g. "1 (12 floors)"')
    ap.add_argument('--floors', type=int, default=5, help='copies of floor 4 to insert (default 5)')
    ap.add_argument('--out-dir', help='where to write the save (default: beside the source)')
    ap.add_argument('--dry-run', action='store_true', help='convert and check, write nothing')
    a = ap.parse_args()
    n = a.floors
    if not 1 <= n <= 10:
        sys.exit('--floors must be between 1 and 10')
    lines = []

    def log(s):
        lines.append(s)
        print(s)

    src = os.path.abspath(a.source)
    base = os.path.basename(src)
    if not base.startswith('Game_') or not base.endswith('.sav'):
        sys.exit('not a game save: ' + base)
    session = base[len('Game_'):-len('.sav')].split('_')[0]
    out_dir = os.path.abspath(a.out_dir or os.path.dirname(src))
    out_name = 'Game_%s_%s.sav' % (session, a.name)
    out_path = os.path.join(out_dir, out_name)
    data_dir = os.path.join(os.path.dirname(out_dir), 'NDMUnofficialPatch-data')
    marker = os.path.join(data_dir, out_name[:-4] + '.floors.txt')
    report = os.path.join(data_dir, out_name[:-4] + '.conversion.txt')
    for p in (out_path, marker):
        if os.path.exists(p) and not a.dry_run:
            sys.exit('already exists, not overwritten: ' + p)

    log('NDM Unofficial Patch, save conversion to %d inserted floors (%d floors in all)' % (n, 7 + n))
    log('source: %s (%d bytes, modified %s)' % (src, os.path.getsize(src), datetime.datetime.fromtimestamp(os.path.getmtime(src))))
    fmt, hdr, body = read_save(src)
    log('body: %d bytes unpacked' % len(body))
    conv = Converter(body, n, log)
    log('pools: %d' % len(conv.pools))
    before = survey(body)
    if sorted(before['data_floors']) != list(range(7)):
        sys.exit('this save does not have seven floors (floor data of floors %s); it may be converted already' % before['data_floors'])
    info = conv.convert()
    new_body = conv.apply()
    new_hdr, old_name = rename_header(hdr, a.name)
    log('save name: %r -> %r' % (old_name, a.name))
    log('new floor data entities %s, new wall map entities %s, floor 4 origin y %g, wall map capacities %s, '
        'worker spawn timer of floor 4 %g' % (info['floor_data_ids'], info['wall_map_ids'], info['origin_y'],
                                               info['wall_map_caps'], info['spawn_timer']))
    for d in info['stairs']:
        log('stair link dropped: ' + d)
    log('changes:')
    for what, c in sorted(conv.counts.items()):
        log('  %6d  %s' % (c, what))
    for w in conv.warnings:
        log('WARNING: ' + w)
    log('floors of entities before: %s' % dict(sorted(before['floors'].items())))
    # Read the result back from the bytes that would be written.
    container = struct.pack('<q', len(fmt)) + fmt + struct.pack('<q', len(new_hdr)) + new_hdr + gzip_bytes(new_body)
    tmp = container
    n1 = struct.unpack_from('<q', tmp, 0)[0]
    n2 = struct.unpack_from('<q', tmp, 8 + n1)[0]
    back = zlib.decompress(tmp[16 + n1 + n2:], 16 + 15)
    if back != new_body:
        sys.exit('the packed body does not unpack to the converted body')
    after = survey(back)
    log('floors of entities after: %s' % dict(sorted(after['floors'].items())))
    log('transform heights by floor band, before %s, after %s' % (dict(sorted(before['heights'].items(), key=str)), dict(sorted(after['heights'].items(), key=str))))
    ok = check(before, after, n, info, log)
    for t in tokens(new_hdr, 0, len(new_hdr), {}):
        pass
    if conv.warnings:
        ok = False
        log('warnings above: nothing written')
    if not ok:
        sys.exit(1)
    links = stair_links(back)
    bad = [l for l in links if l[2] is None or l[1] is None or l[3] is None or not isinstance(l[3], int) or abs(l[1] - l[3]) != 1]
    log('stair links after: %d, all between adjacent floors: %s' % (len(links), 'yes' if not bad else 'no, ' + repr(bad)))
    if bad:
        sys.exit(1)
    log('checks passed')
    if a.dry_run:
        log('dry run: nothing written')
        return
    os.makedirs(data_dir, exist_ok=True)
    with open(out_path, 'xb') as f:
        f.write(container)
    log('written: %s (%d bytes)' % (out_path, len(container)))
    now = datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')
    with open(marker, 'x', encoding='utf-8') as f:
        f.write('# NDM Unofficial Patch: this save was converted from %s to %d inserted floors (%d floors).\n' % (base, n, 7 + n))
        f.write('# The plugin builds floors %d to %d on its first load; save it then to keep them.\n' % (FIRST_MOVED, FIRST_MOVED + n - 1))
        f.write('inserted %d\n' % n)
        f.write('build %d %d\n' % (FIRST_MOVED, FIRST_MOVED + n - 1))
        f.write('converted %s\n' % now)
    log('marker: %s' % marker)
    with open(report, 'x', encoding='utf-8') as f:
        f.write('\n'.join(lines) + '\n')
    print('report: %s' % report)


if __name__ == '__main__':
    main()
