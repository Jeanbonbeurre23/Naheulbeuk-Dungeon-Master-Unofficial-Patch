using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Leopotam.EcsLite;

namespace NDMUnofficialPatch.Common
{
    // Reads fields and ECS component pools straight from IL2CPP memory. Field offsets come from the
    // runtime's own metadata (il2cpp_field_get_offset), never from constants. Used where the generated
    // wrappers for generic types (EcsPool<T>, EcsFilterInject<...>, NativeList<T>) cannot be relied on.
    internal static unsafe class Il2CppRaw
    {
        private static readonly Dictionary<(IntPtr, string), int> Offsets = new();

        // Layout of an IL2CPP array on 64-bit: class pointer, monitor, bounds, length, then the elements.
        private static readonly int ArrayLengthOffset = IntPtr.Size * 3;
        private static readonly int ArrayDataOffset = IntPtr.Size * 4;

        internal static string ClassName(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return "null";
            object name = IL2CPP.il2cpp_class_get_name(IL2CPP.il2cpp_object_get_class(obj));
            return name is IntPtr p ? Marshal.PtrToStringAnsi(p) : name?.ToString();
        }

        internal static int FieldOffset(IntPtr obj, string field)
        {
            IntPtr klass = IL2CPP.il2cpp_object_get_class(obj);
            if (Offsets.TryGetValue((klass, field), out int cached)) return cached;
            for (IntPtr k = klass; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
            {
                IntPtr f = IL2CPP.il2cpp_class_get_field_from_name(k, field);
                if (f == IntPtr.Zero) continue;
                int offset = (int)IL2CPP.il2cpp_field_get_offset(f);
                Offsets[(klass, field)] = offset;
                return offset;
            }
            throw new MissingFieldException(ClassName(obj), field);
        }

        // Reads a pointer-sized field. For the game's injection wrappers (EcsPoolInject, EcsFilterInject,
        // EcsUtilityInject, EcsWorldInject), which hold a single reference, this returns the wrapped object.
        internal static IntPtr ReadPointer(IntPtr obj, string field)
        {
            if (obj == IntPtr.Zero) throw new NullReferenceException($"reading {field} from a null object");
            return *(IntPtr*)(obj + FieldOffset(obj, field));
        }

        // Same, and checks that the object read has the expected class name.
        internal static IntPtr ReadObject(IntPtr obj, string field, string expectedClass)
        {
            IntPtr value = ReadPointer(obj, field);
            string actual = ClassName(value);
            if (actual != expectedClass)
                throw new InvalidOperationException($"{field} holds {actual}, expected {expectedClass}");
            return value;
        }

        // Offset of a field inside a value type's data, as the runtime lays it out (the runtime reports offsets of
        // value-type fields from the start of a boxed object, so the object header is subtracted).
        internal static int ValueFieldOffset<T>(string field)
        {
            IntPtr klass = Il2CppClassPointerStore<T>.NativeClassPtr;
            IntPtr f = klass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_get_field_from_name(klass, field);
            if (f == IntPtr.Zero) throw new MissingFieldException(typeof(T).Name, field);
            return (int)IL2CPP.il2cpp_field_get_offset(f) - 2 * IntPtr.Size;
        }

        internal static void ExpectValueFieldOffset<T>(string field, int expected)
        {
            int actual = ValueFieldOffset<T>(field);
            if (actual != expected)
                throw new InvalidOperationException($"{typeof(T).Name}.{field} is at offset {actual}, expected {expected}; layout differs from game 1.8");
        }

        internal static long ArrayLength(IntPtr array) => array == IntPtr.Zero ? 0 : *(long*)(array + ArrayLengthOffset);
        internal static IntPtr ArrayData(IntPtr array) => array + ArrayDataOffset;
        internal static int ArrayElementSize(IntPtr array) => IL2CPP.il2cpp_array_element_size(IL2CPP.il2cpp_object_get_class(array));
    }

    // One component pool of the game's fork of Leopotam EcsLite, read in place. An entity has the
    // component when _sparseItems[entity] > 0, and the component is then _denseItems[_sparseItems[entity]].
    // The array pointers are read again on every access because the pool reallocates them when it grows.
    internal readonly unsafe struct RawPool
    {
        private readonly IntPtr _pool;
        private readonly int _itemSize;

        private RawPool(IntPtr pool, int itemSize)
        {
            _pool = pool;
            _itemSize = itemSize;
        }

        internal bool IsNull => _pool == IntPtr.Zero;

        // expectedItemSize < 0 skips the size check, for pools used only to test whether an entity has the component.
        internal static RawPool From(IntPtr pool, int expectedItemSize, string what)
        {
            if (pool == IntPtr.Zero) return default;
            string cls = Il2CppRaw.ClassName(pool);
            if (cls != "EcsPool`1") throw new InvalidOperationException($"{what}: object is {cls}, expected EcsPool`1");
            int size = Il2CppRaw.ArrayElementSize(Il2CppRaw.ReadPointer(pool, "_denseItems"));
            if (expectedItemSize >= 0 && size != expectedItemSize)
                throw new InvalidOperationException($"{what}: component size {size} bytes, expected {expectedItemSize}");
            return new RawPool(pool, size);
        }

        internal static RawPool Of<T>(EcsWorld world, int expectedItemSize)
        {
            IEcsPool pool = world.GetPoolByType(Il2CppType.Of<T>());
            return From(pool == null ? IntPtr.Zero : pool.Pointer, expectedItemSize, typeof(T).Name);
        }

        internal long SparseLength => IsNull ? 0 : Il2CppRaw.ArrayLength(Il2CppRaw.ReadPointer(_pool, "_sparseItems"));

        // Address of the entity's component, or IntPtr.Zero when the entity does not have it.
        internal IntPtr Item(int entity)
        {
            if (IsNull || entity < 0) return IntPtr.Zero;
            IntPtr sparse = Il2CppRaw.ReadPointer(_pool, "_sparseItems");
            if (entity >= Il2CppRaw.ArrayLength(sparse)) return IntPtr.Zero;
            int index = *(int*)(Il2CppRaw.ArrayData(sparse) + entity * 4);
            if (index <= 0) return IntPtr.Zero;
            IntPtr dense = Il2CppRaw.ReadPointer(_pool, "_denseItems");
            if (index >= Il2CppRaw.ArrayLength(dense)) return IntPtr.Zero;
            return Il2CppRaw.ArrayData(dense) + index * _itemSize;
        }

        internal bool Has(int entity) => Item(entity) != IntPtr.Zero;

        // Every entity that has the component, in id order. The list is taken at the call, so the caller may change
        // components while going through it.
        internal List<int> Entities()
        {
            var result = new List<int>();
            if (IsNull) return result;
            IntPtr sparse = Il2CppRaw.ReadPointer(_pool, "_sparseItems");
            long length = Il2CppRaw.ArrayLength(sparse);
            IntPtr data = Il2CppRaw.ArrayData(sparse);
            for (int e = 0; e < length; e++)
                if (*(int*)(data + e * 4) > 0) result.Add(e);
            return result;
        }

        // The first entity that has the component, or -1: for pools of singleton components such as the calendar.
        internal int FirstEntity()
        {
            if (IsNull) return -1;
            IntPtr sparse = Il2CppRaw.ReadPointer(_pool, "_sparseItems");
            long length = Il2CppRaw.ArrayLength(sparse);
            IntPtr data = Il2CppRaw.ArrayData(sparse);
            for (int e = 0; e < length; e++)
                if (*(int*)(data + e * 4) > 0) return e;
            return -1;
        }
    }

    internal static class EcsWorldExtensions
    {
        // EcsWorld.GetEntityGen does no bounds check in the game's build, so check the range first.
        internal static bool IsEntityAlive(this EcsWorld world, int entity, int worldSize)
            => entity >= 0 && entity < worldSize && world.GetEntityGen(entity) > 0;

        // The entity a packed reference points to, or -1 when that entity no longer exists.
        internal static int Resolve(this EcsWorld world, EcsPackedEntity packed, int worldSize)
            => world.IsEntityAlive(packed.Id, worldSize) && world.GetEntityGen(packed.Id) == packed.Gen ? packed.Id : -1;
    }
}
