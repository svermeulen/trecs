#if TRECS_INTERNAL_CHECKS && DEBUG

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Trecs.Internal;
using Trecs;
using Trecs.Collections;

namespace Trecs.Serialization
{
    internal sealed class SerializationMemoryTracker
    {
        public class MemoryNode
        {
            public string Name { get; }
            public string TypeName { get; }
            public int BytesWritten { get; set; }
            public List<MemoryNode> Children { get; } = new List<MemoryNode>();
            public MemoryNode Parent { get; }
            public int TypeIdCount { get; set; }
            public int TypeIdBytes { get; set; }

            public MemoryNode(string name, string typeName, MemoryNode parent)
            {
                Name = name;
                TypeName = typeName;
                Parent = parent;
            }

            public int TotalBytes =>
                Children.Count > 0 ? Children.Sum(c => c.TotalBytes) : BytesWritten;
            public int TotalTypeIdBytes => TypeIdBytes + Children.Sum(c => c.TotalTypeIdBytes);
        }

        private MemoryNode _root;
        private MemoryNode _current;
        private readonly Stack<MemoryNode> _nodeStack = new Stack<MemoryNode>();
        private int _headerBytes;
        private int _bitFieldBytes;
        private int _sentinelBytes;
        private readonly DenseDictionary<string, int> _typeIdCounts = new();
        private readonly DenseDictionary<string, int> _typeIdSizes = new();
        private bool _isEnabled;

        public SerializationMemoryTracker() { }

        public bool IsEnabled
        {
            get => _isEnabled;
        }

        public void Reset(bool enabled)
        {
            _isEnabled = enabled;

            if (enabled)
            {
                _root = new MemoryNode("Root", null, null);
            }

            _current = _root;
            _nodeStack.Clear();
            _headerBytes = 0;
            _bitFieldBytes = 0;
            _sentinelBytes = 0;
            _typeIdCounts.Clear();
            _typeIdSizes.Clear();
        }

        public void TrackHeaderBytes(int bytes, string component)
        {
            if (!_isEnabled)
                return;

            if (component == "BitFields")
            {
                _bitFieldBytes += bytes;
            }
            else if (component == "Sentinel")
            {
                _sentinelBytes += bytes;
            }
            else
            {
                _headerBytes += bytes;
            }
        }

        public void TrackTypeId(Type type, int bytes)
        {
            if (!_isEnabled)
                return;

            var typeName = type.GetPrettyName();

            if (!_typeIdCounts.ContainsKey(typeName))
            {
                _typeIdCounts[typeName] = 0;
                _typeIdSizes[typeName] = bytes;
            }
            _typeIdCounts[typeName]++;

            _current.TypeIdCount++;
            _current.TypeIdBytes += bytes;
        }

        public void BeginTrackingField(string fieldName, Type type)
        {
            if (!_isEnabled)
                return;

            var typeName = type.GetPrettyName();
            var node = new MemoryNode(fieldName ?? "unnamed", typeName, _current);
            _current.Children.Add(node);
            _nodeStack.Push(_current);
            _current = node;
        }

        public void EndTrackingField(int bytesWritten)
        {
            if (!_isEnabled)
                return;

            _current.BytesWritten = bytesWritten;
            if (_nodeStack.Count > 0)
            {
                _current = _nodeStack.Pop();
            }
        }

        public void TrackDirectWrite(string fieldName, Type type, int bytes)
        {
            if (!_isEnabled)
                return;

            var typeName = type.GetPrettyName();
            var node = new MemoryNode(fieldName ?? "unnamed", typeName, _current);
            node.BytesWritten = bytes;
            _current.Children.Add(node);
        }

        public string GenerateReport()
        {
            if (!_isEnabled)
                return "Memory tracking is disabled";

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("=== Serialization Memory Breakdown ===");

            int totalDataBytes = _root.TotalBytes;
            int totalTypeIdBytes = _root.TotalTypeIdBytes;
            int totalHeaderBytes = _headerBytes + _bitFieldBytes + _sentinelBytes;
            int totalBytes = totalDataBytes + totalTypeIdBytes + totalHeaderBytes;

            sb.AppendLine($"Total: {totalBytes:N0} bytes");
            sb.AppendLine();

            // Header breakdown
            if (totalHeaderBytes > 0)
            {
                sb.AppendLine(
                    $"Header & Metadata: {totalHeaderBytes} bytes ({GetPercentage(totalHeaderBytes, totalBytes):F1}%)"
                );
                if (_headerBytes > 0)
                    sb.AppendLine($"├─ Version/Flags: {_headerBytes} bytes");
                if (_bitFieldBytes > 0)
                    sb.AppendLine($"├─ Bit fields: {_bitFieldBytes} bytes");
                if (_sentinelBytes > 0)
                    sb.AppendLine($"└─ Sentinel: {_sentinelBytes} bytes");
                sb.AppendLine();
            }

            // Type ID breakdown
            if (totalTypeIdBytes > 0)
            {
                sb.AppendLine(
                    $"Type IDs: {totalTypeIdBytes} bytes ({GetPercentage(totalTypeIdBytes, totalBytes):F1}%)"
                );
                var sortedTypeIds = _typeIdCounts
                    .Where(kvp => kvp.Value * _typeIdSizes[kvp.Key] > 0)
                    .OrderByDescending(kvp => kvp.Value * _typeIdSizes[kvp.Key])
                    .ToList();

                for (int i = 0; i < sortedTypeIds.Count; i++)
                {
                    var kvp = sortedTypeIds[i];
                    var typeBytes = kvp.Value * _typeIdSizes[kvp.Key];
                    var prefix = i == sortedTypeIds.Count - 1 ? "└─" : "├─";
                    sb.AppendLine($"{prefix} {kvp.Key} ({kvp.Value}x): {typeBytes} bytes");
                }
                sb.AppendLine();
            }

            // Data breakdown
            sb.AppendLine(
                $"Serialized Data: {totalDataBytes} bytes ({GetPercentage(totalDataBytes, totalBytes):F1}%)"
            );
            PrintNode(sb, _root, "", true, totalBytes);

            return sb.ToString();
        }

        private void PrintNode(
            StringBuilder sb,
            MemoryNode node,
            string indent,
            bool isLast,
            int totalBytes
        )
        {
            if (node.Parent != null) // Skip root node
            {
                var nodeBytes = node.TotalBytes;

                // Skip nodes with zero bytes (common in delta serialization)
                if (nodeBytes == 0)
                    return;

                var prefix = isLast ? "└─" : "├─";
                var nodeName = GetEnhancedNodeName(node);
                sb.AppendLine(
                    $"{indent}{prefix} {nodeName}: {nodeBytes} bytes ({GetPercentage(nodeBytes, totalBytes):F1}%)"
                );
                indent += isLast ? "   " : "│  ";
            }

            // Sort children by size descending, filter out zero-byte entries
            var sortedChildren = node
                .Children.Where(c => c.TotalBytes > 0)
                .OrderByDescending(c => c.TotalBytes)
                .ToList();

            for (int i = 0; i < sortedChildren.Count; i++)
            {
                PrintNode(sb, sortedChildren[i], indent, i == sortedChildren.Count - 1, totalBytes);
            }
        }

        private string GetEnhancedNodeName(MemoryNode node)
        {
            // Improve naming for better readability
            var fieldName = node.Name;
            var typeName = node.TypeName;

            // For descriptive field names, show both field and type
            if (
                !string.IsNullOrEmpty(typeName)
                && !fieldName.Equals(typeName, StringComparison.OrdinalIgnoreCase)
            )
            {
                return $"{fieldName} ({typeName})";
            }

            // Fallback to original behavior
            return string.IsNullOrEmpty(typeName) ? fieldName : $"{fieldName} ({typeName})";
        }

        private double GetPercentage(int part, int total)
        {
            return total > 0 ? (part * 100.0) / total : 0;
        }
    }
}

#endif
