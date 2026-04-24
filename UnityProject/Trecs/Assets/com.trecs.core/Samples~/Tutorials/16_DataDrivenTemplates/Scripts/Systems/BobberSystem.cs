using Unity.Mathematics;

namespace Trecs.Samples.DataDrivenTemplates
{
    /// <summary>
    /// Bobs entities vertically around their spawn Y. Matches any template that
    /// declares <see cref="Position"/> and <see cref="BobParams"/>.
    /// </summary>
    public partial class BobberSystem : ISystem
    {
        [ForEachEntity(MatchByComponents = true)]
        void Execute(ref Position position, in BobParams bob)
        {
            float t = World.ElapsedTime * bob.Speed;
            position.Value.y = math.sin(t) * bob.Amplitude;
        }
    }
}
