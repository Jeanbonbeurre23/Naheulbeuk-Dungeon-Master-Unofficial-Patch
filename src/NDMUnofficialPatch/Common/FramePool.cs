using System;
using Il2CppInterop.Runtime;
using Leopotam.EcsLite;

namespace NDMUnofficialPatch.Common
{
    // A component pool read in place, for code that looks up many entities in one frame. The array pointers are read
    // once by Refresh, which must be called again in every frame before use, since the pool reallocates its arrays
    // when it grows. Otherwise the rules are those of RawPool: an entity has the component when _sparseItems[entity]
    // is above 0, and the component is then _denseItems[_sparseItems[entity]].
    internal sealed unsafe class FramePool
    {
        private readonly IntPtr _pool;
        private IntPtr _sparse, _dense;
        private long _sparseLength, _denseLength;

        internal readonly int ItemSize;
        internal bool IsNull => _pool == IntPtr.Zero;

        private FramePool(IntPtr pool, int itemSize)
        {
            _pool = pool;
            ItemSize = itemSize;
        }

        // The pool of T in the world, or an empty pool when the world has none.
        internal static FramePool Of<T>(EcsWorld world)
        {
            IEcsPool pool = world.GetPoolByType(Il2CppType.Of<T>());
            IntPtr p = pool == null ? IntPtr.Zero : pool.Pointer;
            if (p == IntPtr.Zero) return new FramePool(IntPtr.Zero, 0);
            string cls = Il2CppRaw.ClassName(p);
            if (cls != "EcsPool`1") throw new InvalidOperationException($"{typeof(T).Name}: object is {cls}, expected EcsPool`1");
            return new FramePool(p, Il2CppRaw.ArrayElementSize(Il2CppRaw.ReadPointer(p, "_denseItems")));
        }

        internal void Refresh()
        {
            if (_pool == IntPtr.Zero) return;
            IntPtr sparse = Il2CppRaw.ReadPointer(_pool, "_sparseItems");
            IntPtr dense = Il2CppRaw.ReadPointer(_pool, "_denseItems");
            _sparse = sparse == IntPtr.Zero ? IntPtr.Zero : Il2CppRaw.ArrayData(sparse);
            _sparseLength = Il2CppRaw.ArrayLength(sparse);
            _dense = dense == IntPtr.Zero ? IntPtr.Zero : Il2CppRaw.ArrayData(dense);
            _denseLength = Il2CppRaw.ArrayLength(dense);
        }

        // Address of the entity's component, or IntPtr.Zero when the entity does not have it.
        internal IntPtr Item(int entity)
        {
            if (_sparse == IntPtr.Zero || entity < 0 || entity >= _sparseLength) return IntPtr.Zero;
            int index = *(int*)(_sparse + entity * 4);
            if (index <= 0 || index >= _denseLength) return IntPtr.Zero;
            return _dense + index * ItemSize;
        }

        internal bool Has(int entity) => Item(entity) != IntPtr.Zero;
    }
}
