using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    /// <summary>
    ///     Finds every segmenting device that participates in a powered closed power loop, for CYCLE_FAULT
    ///     marking (POWER.md §4). Runs in PROTECT (cycle detection) of the atomic tick, before the allocator, so the loop
    ///     is dissolved (each member contributes 0) before ALLOCATE sees its inflated Potential / Required.
    ///
    ///     <para>Model (deviation from POWER.md §4.2.5's undirected bipartite DFS, see POWER_DEVIATIONS.md
    ///     D5): a DIRECTED multigraph where nodes are cable networks and each segmenter contributes one
    ///     directed edge InputNetwork -> OutputNetwork. A power loop is a directed cycle, found via Tarjan's
    ///     strongly-connected-components: a segmenter is on a loop iff its two networks lie in the same SCC
    ///     of size >= 2 (mutually reachable). This avoids the false positive the undirected model has for
    ///     parallel same-direction segmenters (two transformers both stepping net1 -> net2 are normal
    ///     redundancy, not a loop, and are correctly NOT flagged because net2 cannot reach net1).</para>
    ///
    ///     <para>Wireless PT/PR links are represented through the shared WirelessNetwork node
    ///     (PT.OutputNetwork === PR.InputNetwork), so multi-PR fan-out is handled without enumerating pairs.
    ///     Edges are gated on OnOff + both-networks-non-null + Input != Output: an OFF device does not
    ///     conduct (so toggling OFF breaks a loop, the OFF-as-reset path), and a short-circuited device is
    ///     excluded. Fault state does NOT remove an edge (POWER.md §17.39): a faulted-but-on member still
    ///     contributes its edge so the loop keeps being detected and new members join. A detected SCC is
    ///     only faulted if it carries power (min(Potential, Required) > 0 on one of the segmenter's
    ///     networks), per POWER.md §4.4 "only powered loops".</para>
    /// </summary>
    internal static class CycleGraphBuilder
    {
        // Returns the ReferenceIds of every segmenter on a powered directed cycle.
        internal static HashSet<long> FindCycleFaultedSegmenters(Core.GridSnapshot snap)
        {
            var result = new HashSet<long>();
            if (snap == null || snap.SegmentersSorted.Count == 0) return result;

            // Dense node indexing: network ReferenceId -> contiguous index.
            var nodeIndex = new Dictionary<long, int>();
            var nodeNet = new List<CableNetwork>();
            var adj = new List<List<int>>();

            int GetOrAddNode(CableNetwork net)
            {
                if (nodeIndex.TryGetValue(net.ReferenceId, out var idx)) return idx;
                idx = nodeNet.Count;
                nodeIndex[net.ReferenceId] = idx;
                nodeNet.Add(net);
                adj.Add(new List<int>());
                return idx;
            }

            // Directed edges, one per conducting segmenter.
            var edgeFrom = new List<int>();
            var edgeTo = new List<int>();
            var edgeSeg = new List<long>();

            // Edges come from the snapshot rows (control and terminals captured at the boundary
            // read), so the graph is built from the same instant the adapters billed under; a
            // segmenter appears on both its terminal nets' rows, hence the dedup set.
            var seenEdges = new HashSet<long>();
            for (int ni = 0; ni < snap.Nets.Count; ni++)
            {
                var rows = snap.Nets[ni].Rows;
                for (int ri = 0; ri < rows.Count; ri++)
                {
                    var row = rows[ri];
                    if (!row.IsSegmenter || !seenEdges.Add(row.RefId)) continue;
                    if (!row.OnOff) continue;                    // OFF device does not conduct
                    var inNet = row.SegInputNet;
                    var outNet = row.SegOutputNet;
                    if (inNet == null || outNet == null) continue;
                    if (inNet.ReferenceId == outNet.ReferenceId) continue;   // short-circuit / same net

                    int from = GetOrAddNode(inNet);
                    int to = GetOrAddNode(outNet);
                    adj[from].Add(to);
                    edgeFrom.Add(from);
                    edgeTo.Add(to);
                    edgeSeg.Add(row.RefId);
                }
            }

            if (edgeSeg.Count == 0) return result;

            // Tarjan SCC (iterative, to stay off the recursion stack on the worker thread).
            int v = nodeNet.Count;
            var sccId = TarjanScc(v, adj, out var sccSize);

            // A segmenter is on a loop iff its two networks share an SCC of size >= 2. Fault it only if
            // the loop carries power on one of its networks.
            for (int e = 0; e < edgeSeg.Count; e++)
            {
                int from = edgeFrom[e];
                int to = edgeTo[e];
                if (sccId[from] != sccId[to]) continue;
                if (sccSize[sccId[from]] < 2) continue;
                if (IsPowered(snap, nodeNet[from]) || IsPowered(snap, nodeNet[to]))
                    result.Add(edgeSeg[e]);
            }

            return result;
        }

        // "Carries power" from the snapshot's vanilla-OBSERVE-equivalent per-net sums (the
        // segmenter surfaces serve the previous tick's published totals at the boundary read,
        // exactly what the retired PowerTick.CalculateState produced here).
        private static bool IsPowered(Core.GridSnapshot snap, CableNetwork net)
        {
            if (net == null || !snap.ById.TryGetValue(net.ReferenceId, out var nr)) return false;
            float actual = nr.PotentialSum < nr.RequiredSum ? nr.PotentialSum : nr.RequiredSum;
            return actual > 0f;
        }

        // Iterative Tarjan. Returns an array sccId[node]; sccSize maps sccId -> member count.
        private static int[] TarjanScc(int n, List<List<int>> adj, out int[] sccSize)
        {
            var index = new int[n];
            var lowlink = new int[n];
            var onStack = new bool[n];
            var visited = new bool[n];
            var sccId = new int[n];
            for (int i = 0; i < n; i++) { index[i] = -1; sccId[i] = -1; }

            var sccSizes = new List<int>();
            var tarjanStack = new Stack<int>();
            int nextIndex = 0;
            int nextScc = 0;

            // Explicit work stack of (node, next-child-cursor).
            var workNode = new Stack<int>();
            var workCursor = new Stack<int>();

            for (int root = 0; root < n; root++)
            {
                if (visited[root]) continue;
                workNode.Push(root);
                workCursor.Push(0);

                while (workNode.Count > 0)
                {
                    int node = workNode.Peek();
                    int cursor = workCursor.Pop();

                    if (cursor == 0)
                    {
                        visited[node] = true;
                        index[node] = lowlink[node] = nextIndex++;
                        tarjanStack.Push(node);
                        onStack[node] = true;
                    }

                    bool recursed = false;
                    var succ = adj[node];
                    while (cursor < succ.Count)
                    {
                        int w = succ[cursor];
                        cursor++;
                        if (!visited[w])
                        {
                            workCursor.Push(cursor);   // resume node after this child returns
                            workNode.Push(w);
                            workCursor.Push(0);
                            recursed = true;
                            break;
                        }
                        if (onStack[w] && index[w] < lowlink[node])
                            lowlink[node] = index[w];
                    }

                    if (recursed) continue;

                    // Finished all children of node.
                    if (lowlink[node] == index[node])
                    {
                        int size = 0;
                        while (true)
                        {
                            int w = tarjanStack.Pop();
                            onStack[w] = false;
                            sccId[w] = nextScc;
                            size++;
                            if (w == node) break;
                        }
                        sccSizes.Add(size);
                        nextScc++;
                    }

                    workNode.Pop();   // node done
                    if (workNode.Count > 0)
                    {
                        int parent = workNode.Peek();
                        if (lowlink[node] < lowlink[parent]) lowlink[parent] = lowlink[node];
                    }
                }
            }

            sccSize = sccSizes.ToArray();
            return sccId;
        }
    }
}
