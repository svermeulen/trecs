using Unity.Mathematics;

namespace Trecs.Samples.SaveGame
{
    /// <summary>
    /// Parses the hardcoded Sokoban level string into wall, box, target,
    /// and player entities. Level geometry (walls, targets) is part of
    /// ECS state and participates in bookmark save/load — load a bookmark
    /// and the whole level reverts.
    ///
    /// Legend: '#' wall, '.' empty, 'P' player, 'B' box, 'T' target.
    /// </summary>
    public class SaveGameSceneInitializer
    {
        // Rows are listed top-down (row 0 is the top of the board). The
        // initializer flips Y so the rendered board matches this layout.
        static readonly string[] Level = new[]
        {
            "########",
            "#......#",
            "#..T...#",
            "#...#..#",
            "#.B.B..#",
            "#P..T..#",
            "#.B..T.#",
            "########",
        };

        public static int GridSize => Level.Length;

        readonly WorldAccessor _world;

        public SaveGameSceneInitializer(World world)
        {
            _world = world.CreateAccessor();
        }

        public void Initialize()
        {
            int height = Level.Length;
            for (int row = 0; row < height; row++)
            {
                var line = Level[row];
                int y = height - 1 - row;
                for (int x = 0; x < line.Length; x++)
                {
                    var pos = new int2(x, y);
                    switch (line[x])
                    {
                        case '#':
                            _world.AddEntity<SaveGameTags.Wall>().Set(new GridPos(pos));
                            break;
                        case 'P':
                            _world.AddEntity<SaveGameTags.Player>().Set(new GridPos(pos));
                            break;
                        case 'B':
                            _world.AddEntity<SaveGameTags.Box>().Set(new GridPos(pos));
                            break;
                        case 'T':
                            _world.AddEntity<SaveGameTags.Target>().Set(new GridPos(pos));
                            break;
                        case '.':
                        case ' ':
                            break;
                    }
                }
            }
        }
    }
}
