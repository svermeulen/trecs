namespace Trecs.Internal
{
    public static class HashUtil
    {
        // Boost hash_combine — uses the golden ratio constant (0x9e3779b9) for mixing.
        // See boost::hash_combine in boost/container_hash/hash.hpp
        public static int CombineHashes(int hash, int value)
        {
            unchecked
            {
                return hash ^ (value + (int)0x9e3779b9 + (hash << 6) + (hash >> 2));
            }
        }
    }
}
