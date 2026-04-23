namespace Trecs.Internal
{
#if DEBUG
    struct ECSTuple<T1, T2>
    {
        public readonly T1 instance;
        public T2 numberOfImplementations;

        public ECSTuple(T1 implementor, T2 v)
        {
            instance = implementor;
            numberOfImplementations = v;
        }
    }
#endif
}
