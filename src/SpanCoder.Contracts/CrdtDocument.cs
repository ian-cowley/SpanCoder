using System;
using System.Collections.Generic;
using System.Text;

namespace SpanCoder.Contracts
{
    public class PositionComparer : IComparer<int[]>
    {
        public static readonly PositionComparer Instance = new PositionComparer();

        public int Compare(int[]? x, int[]? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            int len = Math.Min(x.Length, y.Length);
            for (int i = 0; i < len; i++)
            {
                if (x[i] != y[i])
                    return x[i].CompareTo(y[i]);
            }
            return x.Length.CompareTo(y.Length);
        }
    }

    public class CrdtDocument
    {
        public const int Base = 10000;
        
        public string ClientId { get; }
        public int Clock { get; private set; }
        public List<CrdtNodeState> Nodes { get; } = new List<CrdtNodeState>();
        private readonly object _lock = new object();

        public CrdtDocument(string clientId)
        {
            ClientId = clientId;
            Clock = 0;
        }

        public void InitializeFromState(List<CrdtNodeState> states)
        {
            lock (_lock)
            {
                Nodes.Clear();
                Nodes.AddRange(states);
                SortNodes();
            }
        }

        public List<CrdtNodeState> GetState()
        {
            lock (_lock)
            {
                var copy = new List<CrdtNodeState>(Nodes.Count);
                foreach (var node in Nodes)
                {
                    copy.Add(new CrdtNodeState
                    {
                        Position = (int[])node.Position.Clone(),
                        Value = node.Value,
                        ClientId = node.ClientId,
                        Clock = node.Clock,
                        IsDeleted = node.IsDeleted
                    });
                }
                return copy;
            }
        }

        public CrdtNodeState LocalInsert(int visibleOffset, char val)
        {
            lock (_lock)
            {
                Clock++;
                
                int[]? prevPos = null;
                int[]? nextPos = null;

                // Find left (prev) and right (next) non-deleted nodes
                int visibleCount = 0;
                CrdtNodeState? leftNode = null;
                CrdtNodeState? rightNode = null;

                for (int i = 0; i < Nodes.Count; i++)
                {
                    var node = Nodes[i];
                    if (!node.IsDeleted)
                    {
                        if (visibleCount == visibleOffset - 1)
                        {
                            leftNode = node;
                        }
                        if (visibleCount == visibleOffset)
                        {
                            rightNode = node;
                            break;
                        }
                        visibleCount++;
                    }
                }

                // If leftNode wasn't found because offset is 0, prevPos is null.
                // If rightNode wasn't found because offset is at the end, nextPos is null.
                if (leftNode != null) prevPos = leftNode.Position;
                if (rightNode != null) nextPos = rightNode.Position;

                int[] newPos = GeneratePositionBetween(prevPos, nextPos);

                var newNode = new CrdtNodeState
                {
                    Position = newPos,
                    Value = val,
                    ClientId = ClientId,
                    Clock = Clock,
                    IsDeleted = false
                };

                InsertSorted(newNode);
                return newNode;
            }
        }

        public CrdtNodeState? LocalDelete(int visibleOffset)
        {
            lock (_lock)
            {
                int visibleCount = 0;
                for (int i = 0; i < Nodes.Count; i++)
                {
                    var node = Nodes[i];
                    if (!node.IsDeleted)
                    {
                        if (visibleCount == visibleOffset)
                        {
                            node.IsDeleted = true;
                            Clock++;
                            return node;
                        }
                        visibleCount++;
                    }
                }
                return null;
            }
        }

        public bool ApplyRemoteInsert(CollabInsertMessage msg)
        {
            lock (_lock)
            {
                // Check if node already exists
                var existing = FindNodeIndex(msg.Position, msg.ClientId);
                if (existing >= 0) return false;

                var newNode = new CrdtNodeState
                {
                    Position = msg.Position,
                    Value = msg.Value,
                    ClientId = msg.ClientId,
                    Clock = msg.Clock,
                    IsDeleted = false
                };

                InsertSorted(newNode);
                return true;
            }
        }

        public bool ApplyRemoteDelete(CollabDeleteMessage msg)
        {
            lock (_lock)
            {
                int idx = FindNodeIndex(msg.Position, msg.ClientId);
                if (idx >= 0)
                {
                    var node = Nodes[idx];
                    if (!node.IsDeleted)
                    {
                        node.IsDeleted = true;
                        return true;
                    }
                }
                return false;
            }
        }

        public int GetVisibleOffsetOf(int[] position, string clientId)
        {
            lock (_lock)
            {
                int visibleOffset = 0;
                for (int i = 0; i < Nodes.Count; i++)
                {
                    var node = Nodes[i];
                    if (PositionComparer.Instance.Compare(node.Position, position) == 0 && node.ClientId == clientId)
                    {
                        return node.IsDeleted ? -1 : visibleOffset;
                    }
                    if (!node.IsDeleted)
                    {
                        visibleOffset++;
                    }
                }
                return -1;
            }
        }

        public string GetText()
        {
            lock (_lock)
            {
                var sb = new StringBuilder(Nodes.Count);
                foreach (var node in Nodes)
                {
                    if (!node.IsDeleted)
                    {
                        sb.Append(node.Value);
                    }
                }
                return sb.ToString();
            }
        }

        public static int[] GeneratePositionBetween(int[]? prev, int[]? next)
        {
            var A = prev ?? new int[] { 0 };
            var B = next ?? new int[] { Base };

            var result = new List<int>();
            int i = 0;
            while (true)
            {
                int a = i < A.Length ? A[i] : 0;
                int b = i < B.Length ? B[i] : Base;

                if (a == b)
                {
                    result.Add(a);
                    i++;
                    continue;
                }

                if (b - a > 1)
                {
                    result.Add(a + (b - a) / 2);
                    break;
                }
                else
                {
                    result.Add(a);
                    // Force upper bound to be Base for subsequent elements
                    B = new int[i + 2];
                    Array.Copy(prev ?? new int[] { 0 }, B, Math.Min(i + 1, (prev ?? new int[] { 0 }).Length));
                    B[i] = a;
                    B[i + 1] = Base;
                    i++;
                }
            }
            return result.ToArray();
        }

        private void InsertSorted(CrdtNodeState node)
        {
            int idx = Nodes.BinarySearch(node, NodeComparer.Instance);
            if (idx < 0)
            {
                Nodes.Insert(~idx, node);
            }
            else
            {
                Nodes.Insert(idx, node);
            }
        }

        private int FindNodeIndex(int[] position, string clientId)
        {
            var dummy = new CrdtNodeState { Position = position, ClientId = clientId };
            int idx = Nodes.BinarySearch(dummy, NodeComparer.Instance);
            if (idx >= 0)
            {
                // Verify exact match (BinarySearch on NodeComparer handles sorting order, but double-check)
                var found = Nodes[idx];
                if (found.ClientId == clientId && PositionComparer.Instance.Compare(found.Position, position) == 0)
                {
                    return idx;
                }
            }
            // Fallback scan if binary search missed due to concurrent updates
            for (int i = 0; i < Nodes.Count; i++)
            {
                var n = Nodes[i];
                if (n.ClientId == clientId && PositionComparer.Instance.Compare(n.Position, position) == 0)
                {
                    return i;
                }
            }
            return -1;
        }

        private void SortNodes()
        {
            Nodes.Sort(NodeComparer.Instance);
        }

        private class NodeComparer : IComparer<CrdtNodeState>
        {
            public static readonly NodeComparer Instance = new NodeComparer();

            public int Compare(CrdtNodeState? x, CrdtNodeState? y)
            {
                if (x == null && y == null) return 0;
                if (x == null) return -1;
                if (y == null) return 1;

                int cmp = PositionComparer.Instance.Compare(x.Position, y.Position);
                if (cmp != 0) return cmp;
                return string.Compare(x.ClientId, y.ClientId, StringComparison.Ordinal);
            }
        }
    }
}
