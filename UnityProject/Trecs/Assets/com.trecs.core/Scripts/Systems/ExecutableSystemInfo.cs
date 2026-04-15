namespace Trecs
{
    public class ExecutableSystemInfo
    {
        public ExecutableSystemInfo(ISystem system, SystemMetadata metadata, int declarationIndex)
        {
            System = system;
            Metadata = metadata;
            DeclarationIndex = declarationIndex;
        }

        public ISystem System { get; }
        public SystemMetadata Metadata { get; }
        public int DeclarationIndex { get; }
    }
}
