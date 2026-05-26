using System;
using System.Collections.Generic;
using System.Linq;
using Trecs.Collections;

namespace Trecs.Internal
{
    public sealed class TopologicalSorter
    {
        /// <summary>
        /// Note - int[] should be same length for all items
        /// </summary>
        public static List<int> Run<T>(
            List<T> items,
            Func<T, ReadOnlyIterableHashSet<int>> getDependencies,
            Func<T, int[]> getSortKeys,
            Func<T, string> itemToString
        )
        {
            // Step 1: Perform topological sort
            Dictionary<int, List<int>> graph = new();
            int[] inDegree = new int[items.Count];

            // Initialize graph for all indices
            for (int i = 0; i < items.Count; i++)
            {
                graph[i] = new List<int>();
            }

            for (int i = 0; i < items.Count; i++)
            {
                foreach (var dep in getDependencies(items[i]))
                {
                    if (dep < 0 || dep >= items.Count)
                    {
                        throw new ArgumentException(
                            $"Invalid dependency index {dep} for item at index {i}"
                        );
                    }

                    graph[dep].Add(i);
                    inDegree[i]++;
                }
            }

            // Using a priority queue to maintain order based on some optional additional criteria
            SortedSet<(int index, int[] keys)> queue = new(
                Comparer<(int, int[])>.Create(
                    (x, y) =>
                    {
                        for (int i = 0; i < x.Item2.Length; i++)
                        {
                            int result = x.Item2[i].CompareTo(y.Item2[i]);
                            if (result != 0)
                            {
                                return result;
                            }
                        }

                        return x.Item1.CompareTo(y.Item1);
                    }
                )
            );

            for (int i = 0; i < items.Count; i++)
            {
                if (inDegree[i] == 0)
                {
                    queue.Add((i, getSortKeys(items[i])));
                }
            }

            List<int> result = new();

            while (queue.Count > 0)
            {
                var (node, _) = queue.Min;
                queue.Remove(queue.Min);
                result.Add(node);

                foreach (var neighbor in graph[node])
                {
                    inDegree[neighbor]--;
                    if (inDegree[neighbor] == 0)
                    {
                        queue.Add((neighbor, getSortKeys(items[neighbor])));
                    }
                }
            }

            if (result.Count != items.Count)
            {
                HashSet<int> visited = new();
                Stack<int> recursionStack = new();

                for (int i = 0; i < items.Count; i++)
                {
                    if (DetectCycle(i, visited, recursionStack, graph, items, out var cycleNodes))
                    {
                        throw new InvalidOperationException(
                            $"Found a circular dependency in the system graph! Cycle:\n  {GetCycleString(cycleNodes, items, itemToString)}"
                        );
                    }
                }

                // Fallback: cycle detected but no specific nodes identified
                throw new InvalidOperationException(
                    "Found a circular dependency in the system graph (cycle path could not be extracted)"
                );
            }

            return result;
        }

        static bool DetectCycle<T>(
            int node,
            HashSet<int> visited,
            Stack<int> recursionStack,
            Dictionary<int, List<int>> graph,
            List<T> items,
            out List<int> cycleNodes
        )
        {
            if (recursionStack.Contains(node))
            {
                var stackList = recursionStack.ToList();
                var cycleStart = stackList.IndexOf(node);
                cycleNodes = stackList.GetRange(0, cycleStart + 1);
                return true;
            }

            if (visited.Contains(node))
            {
                cycleNodes = null;
                return false;
            }

            visited.Add(node);
            recursionStack.Push(node);

            foreach (var neighbor in graph[node])
            {
                if (DetectCycle(neighbor, visited, recursionStack, graph, items, out cycleNodes))
                    return true;
            }

            var poppedValue = recursionStack.Pop();
            TrecsDebugAssert.IsEqual(node, poppedValue);
            cycleNodes = null;
            return false;
        }

        static string GetCycleString<T>(
            List<int> cycle,
            List<T> items,
            Func<T, string> itemToString
        )
        {
            return string.Join(
                "\n  -> ",
                cycle
                    .Select(index => $"{itemToString(items[index])}")
                    .Append($"{itemToString(items[cycle.First()])}")
            );
        }
    }
}
