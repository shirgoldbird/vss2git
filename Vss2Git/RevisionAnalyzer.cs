﻿/* Copyright 2009 HPDI, LLC
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Hpdi.VssLogicalLib;
using Hpdi.VssPhysicalLib;

namespace Hpdi.Vss2Git
{
    /// <summary>
    /// Enumerates revisions in a VSS database.
    /// </summary>
    /// <author>Trevor Robinson</author>
    class RevisionAnalyzer : Worker
    {
        private string excludeFiles;
        public string ExcludeFiles
        {
            get { return excludeFiles; }
            set { excludeFiles = value; }
        }

        private bool ignoreSortingErrors;
        public bool IgnoreSortingErrors
        {
            get { return ignoreSortingErrors; }
            set { ignoreSortingErrors = value; }
        }

        private readonly VssDatabase database;
        public VssDatabase Database
        {
            get { return database; }
        }

        private readonly LinkedList<VssProject> rootProjects = new LinkedList<VssProject>();
        public IEnumerable<VssProject> RootProjects
        {
            get { return rootProjects; }
        }

        private class SortableRevision : IComparable<SortableRevision>
        {
            public Revision revision;
            public int traversalNumber;
            public DateTime dateTime;

            public SortableRevision(Revision revision, int traversalNumber, DateTime dateTime)
            {
                this.revision = revision;
                this.traversalNumber = traversalNumber;
                this.dateTime = dateTime;
            }

            public SortableRevision(Revision revision, int traversalNumber)
                : this(revision, traversalNumber, revision.DateTime)
            { }

            public int CompareTo(SortableRevision other)
            {
                // first sorting criteria: time
                int cmp = dateTime.CompareTo(other.dateTime);

                // second sorting criteria: user (unlikely, but if it happens...)
                if (cmp == 0)
                {
                    cmp = string.Compare(revision.User, other.revision.User, StringComparison.Ordinal);
                }

                // third sorting criteria: consecutive number from database traversal
                if (cmp == 0)
                {
                    cmp = traversalNumber.CompareTo(other.traversalNumber);
                }

                return cmp;
            }

            public bool IsSameTimeGroup(SortableRevision other)
            {
                return Math.Abs((dateTime - other.dateTime).TotalSeconds) <= 1 &&
                       revision.User == other.revision.User;
            }

            public override string ToString()
            {
                return string.Format("#{0:D5} {1:yyyy-MM-ddTHH:mm:ss.FFFFFFF}{2} {3}:{4} [{5}] {6}{7}",
                    traversalNumber,
                    revision.DateTime,
                    revision.DateTime == dateTime ? "" : $" = {dateTime:yyyy-MM-ddTHH:mm:ss.FFFFFFF}",
                    revision.Item, revision.Version, revision.Action,
                    revision.User,
                    revision.Comment == null
                        ? ""
                        : " \"" + Regex.Replace(revision.Comment, @"[\p{Cc}\\]",
                              a => a.Value == @"\" ? @"\\" : $"\\x{(byte) a.Value[0]:X2}") + "\"");
            }
        }

        private List<SortableRevision> sortedRevisions = new List<SortableRevision>();
        public ICollection<Revision> SortedRevisions
        {
            get { return sortedRevisions.Select(s => s.revision).ToList(); }
        }

        private readonly HashSet<string> processedFiles = new HashSet<string>();
        public HashSet<string> ProcessedFiles
        {
            get { return processedFiles; }
        }

        private readonly HashSet<string> destroyedFiles = new HashSet<string>();
        public HashSet<string> DestroyedFiles
        {
            get { return destroyedFiles; }
        }

        private int projectCount;
        public int ProjectCount
        {
            get { return Thread.VolatileRead(ref projectCount); }
        }

        private int fileCount;
        public int FileCount
        {
            get { return Thread.VolatileRead(ref fileCount); }
        }

        private int revisionCount;
        public int RevisionCount
        {
            get { return Thread.VolatileRead(ref revisionCount); }
        }

        public RevisionAnalyzer(WorkQueue workQueue, Logger logger, VssDatabase database)
            : base(workQueue, logger)
        {
            this.database = database;
        }

        public bool IsDestroyed(string physicalName)
        {
            return destroyedFiles.Contains(physicalName);
        }

        public void AddItem(VssProject project)
        {
            if (project == null)
            {
                throw new ArgumentNullException("project");
            }
            else if (project.Database != database)
            {
                throw new ArgumentException("Project database mismatch", "project");
            }

            rootProjects.AddLast(project);

            PathMatcher exclusionMatcher = null;
            if (!string.IsNullOrEmpty(excludeFiles))
            {
                var excludeFileArray = excludeFiles.Split(
                    new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                exclusionMatcher = new PathMatcher(excludeFileArray);
            }

            workQueue.AddLast(delegate(object work)
            {
                logger.WriteSectionSeparator();
                LogStatus(work, "Building revision list");

                logger.WriteLine("Root project: {0}", project.Path);
                logger.WriteLine("Excluded files: {0}", excludeFiles);

                int excludedProjects = 0;
                int excludedFiles = 0;
                var stopwatch = Stopwatch.StartNew();
                VssUtil.RecurseItems(project,
                    delegate(VssProject subproject)
                    {
                        if (workQueue.IsAborting)
                        {
                            return RecursionStatus.Abort;
                        }

                        var path = subproject.Path;
                        if (exclusionMatcher != null && exclusionMatcher.Matches(path))
                        {
                            logger.WriteLine("Excluding project {0}", path);
                            ++excludedProjects;
                            return RecursionStatus.Skip;
                        }

                        logger.WriteLine("Found project {2} {0}:{1}", path,
                            subproject.Revisions.Select(r => r.Version.ToString()).Aggregate((i, j) => i + "," + j),
                            subproject.Revisions.First().DateTime);

                        ProcessItem(subproject, path, exclusionMatcher);
                        ++projectCount;
                        return RecursionStatus.Continue;
                    },
                    delegate(VssProject subproject, VssFile file)
                    {
                        if (workQueue.IsAborting)
                        {
                            return RecursionStatus.Abort;
                        }

                        var path = file.GetPath(subproject);
                        if (exclusionMatcher != null && exclusionMatcher.Matches(path))
                        {
                            logger.WriteLine("Excluding file {0}", path);
                            ++excludedFiles;
                            return RecursionStatus.Skip;
                        }

                        logger.WriteLine("Found file {2} {0}:{1}", path,
                            file.Revisions.Select(r => r.Version.ToString()).Aggregate((i, j) => i + "," + j),
                            file.Revisions.First().DateTime);

                        // only process shared files once (projects are never shared)
                        if (!processedFiles.Contains(file.PhysicalName))
                        {
                            processedFiles.Add(file.PhysicalName);
                            ProcessItem(file, path, exclusionMatcher);
                            ++fileCount;
                        }
                        return RecursionStatus.Continue;
                    });

                SortRevisions(ignoreSortingErrors);

                int nr = 0;
                foreach (var s in sortedRevisions)
                {
                    logger.WriteLine("Revision {0} {1}", ++nr, s);
                }

                stopwatch.Stop();

                logger.WriteSectionSeparator();
                logger.WriteLine("Analysis complete in {0}", TimeSpan.FromSeconds(Math.Floor(stopwatch.Elapsed.TotalSeconds)));
                logger.WriteLine("Projects: {0} ({1} excluded)", projectCount, excludedProjects);
                logger.WriteLine("Files: {0} ({1} excluded)", fileCount, excludedFiles);
                logger.WriteLine("Revisions: {0}", revisionCount);
            });
        }

        private void ProcessItem(VssItem item, string path, PathMatcher exclusionMatcher)
        {
            try
            {
                foreach (VssRevision vssRevision in item.Revisions)
                {
                    var actionType = vssRevision.Action.Type;
                    var namedAction = vssRevision.Action as VssNamedAction;
                    if (namedAction != null)
                    {
                        if (actionType == VssActionType.Destroy)
                        {
                            // track destroyed files so missing history can be anticipated
                            // (note that Destroy actions on shared files simply delete
                            // that copy, so destroyed files can't be completely ignored)
                            destroyedFiles.Add(namedAction.Name.PhysicalName);
                        }

                        var targetPath = path + VssDatabase.ProjectSeparator + namedAction.Name.LogicalName;
                        if (exclusionMatcher != null && exclusionMatcher.Matches(targetPath))
                        {
                            // project action targets an excluded file
                            continue;
                        }
                    }

                    Revision revision = new Revision(vssRevision.DateTime,
                        vssRevision.User, item.ItemName, vssRevision.Version,
                        vssRevision.Comment, vssRevision.Action);

                    sortedRevisions.Add(new SortableRevision(revision, ++revisionCount));
                }
            }
            catch (RecordException e)
            {
                var message = string.Format("Failed to read revisions for {0} ({1}): {2}",
                    path, item.PhysicalName, ExceptionFormatter.Format(e));
                LogException(e, message);
                ReportError(message);
            }
        }

        private void SortRevisions(bool tolerateErrors)
        {
            // we must not trust timestamp of revisions, because
            // * revisions may have been created from a computer with a wrong system time
            // * revision may have been created in a different time zone
            // * importing from .ssa files creates restore and create revisions at the time
            //   of the import operation, i.e. it does not restore these actions
            //   at the time they have been before export of .ssa file
            //
            // therefore we try to set important constraints on the order of revisions
            // and try to reconstruct a consistent order in which
            // time is only a secondary sort criteria.
            // Order in which the revisions have been created during traversal
            // is used as a tertiary sort criteria to achieve a stable sort
            // (and because traversal order is more reasonable than random order).

            // set database creation time so it will be the first revision
            var createRevisionsByItem = sortedRevisions
                .Where(s => s.revision.Action.Type == VssActionType.Create)
                .ToDictionary(s => s.revision.Item.PhysicalName, s => s);
            if (createRevisionsByItem.ContainsKey(VssDatabase.RootProjectFile))
            {
                createRevisionsByItem[VssDatabase.RootProjectFile].dateTime = DateTime.MinValue;
            }

            sortedRevisions.Sort();

            var graph = new OrderingGraph<SortableRevision>(sortedRevisions);

            // "restore item" must be done before "create item";
            // the project the "restore item" is applied to must be rooted at the time of the restore
            // (tbd: other operations may be done on not-rooted projects if the project was moved?)
            var restoreRevisions = sortedRevisions
                .Where(r => r.revision.Action.Type == VssActionType.Restore)
                .ToList();
            foreach (var restoreRevision in restoreRevisions)
            {
                var item = ((VssRestoreAction) restoreRevision.revision.Action).Name.PhysicalName;
                if (createRevisionsByItem.ContainsKey(item))
                {
                    graph.AddOrderEdge(restoreRevision, createRevisionsByItem[item]);

                    // rooted means the project is already added to a parent project;
                    // if there are multiple "add project" (is this possible?), we choose the one with smallest traversal number
                    var project = restoreRevision.revision.Item;
                    do
                    {
                        Debug.Assert(project.IsProject);
                        var addRevision = sortedRevisions.Where(s =>
                                s.revision.Action.Type == VssActionType.Add &&
                                ((VssAddAction) s.revision.Action).Name.PhysicalName == project.PhysicalName)
                            .OrderBy(s => s.traversalNumber).FirstOrDefault();
                        Debug.Assert(addRevision != null || project.PhysicalName == VssDatabase.RootProjectFile);
                        if (addRevision != null)
                        {
                            graph.AddOrderEdge(addRevision, restoreRevision);
                        }

                        project = addRevision?.revision.Item;
                    } while (project != null);
                }
            }

            // "create item" must be done before "add item", "share item" or any other action that references item;
            foreach (var notRestoreRevision in sortedRevisions.Where(r => r.revision.Action.Type != VssActionType.Restore))
            {
                ICollection<string> items = GetPhysicalNamesReferencedBy(notRestoreRevision);
                foreach (var item in items)
                {
                    if (createRevisionsByItem.ContainsKey(item))
                    {
                        graph.AddOrderEdge(createRevisionsByItem[item], notRestoreRevision);
                    }
                }
            }

            // "destroy project" must be done after any other action that references project
            // Note: currently *not* adding restrictions for "destroy file" here,
            // because it is not final in case the file is shared.
            var destroyRevisionsByProject = sortedRevisions
                .Where(s => s.revision.Action.Type == VssActionType.Destroy && ((VssDestroyAction)s.revision.Action).Name.IsProject)
                .ToDictionary(s => ((VssDestroyAction)s.revision.Action).Name.PhysicalName, s => s);
            if (destroyRevisionsByProject.Any())
            {
                // Note: if a destroy was successful, we still see the item mentioned in projects where it was added, destroyed etc.
                foreach (var revision in sortedRevisions)
                {
                    ICollection<string> items = GetPhysicalNamesReferencedBy(revision);
                    foreach (var item in items)
                    {
                        if (destroyRevisionsByProject.ContainsKey(item) && revision != destroyRevisionsByProject[item])
                        {
                            graph.AddOrderEdge(revision, destroyRevisionsByProject[item]);
                        }
                    }
                }
            }

            // revision versions must be increasing for each item
            var sortedRevisionsByItem = sortedRevisions
                .GroupBy(s => s.revision.Item.PhysicalName)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderBy(s => s.revision.Version).ToList());
            foreach (var item in sortedRevisionsByItem.Keys)
            {
                var revisions = sortedRevisionsByItem[item];
                for (int i = 1; i < revisions.Count; i++)
                {
                    graph.AddOrderEdge(revisions[i-1], revisions[i]);
                }
            }

            // time groups are buddies;
            // buddy groups are transitive, i.e. it is enough to connect consecutive revisions;
            // to handle cases where two users modify the database with the same system time on their computer
            // we split the sequence by user before testing consecutive revisions;
            // since a restore action and accompanying create and add actions have "wrong" time
            // and may have to be re-ordered independently from each other,
            // time groups with restore actions are *not* buddies.
            foreach (var userRevisions in sortedRevisions.GroupBy(s => s.revision.User))
            {
                var timeGroups = new List<List<SortableRevision>>();
                foreach (var revision in userRevisions)
                {
                    Debug.Assert(timeGroups.Count == 0 || timeGroups.Last().Last().CompareTo(revision) < 0);

                    if (timeGroups.Count == 0 || !timeGroups.Last().Last().IsSameTimeGroup(revision))
                    {
                        timeGroups.Add(new List<SortableRevision>());
                    }

                    timeGroups.Last().Add(revision);
                }

                foreach (var timeGroup in timeGroups)
                {
                    if (timeGroup.All(s => s.revision.Action.Type != VssActionType.Restore))
                    {
                        for (int i = 1; i < timeGroup.Count; i++)
                        {
                            graph.AddBuddyEdge(timeGroup[i - 1], timeGroup[i]);
                        }
                    }
                }

            }

            int nrErrors;
            var advancedSortedRevisions = graph.Sort(out nrErrors);
            Debug.Assert(sortedRevisions.Count == advancedSortedRevisions.Count);
            logger.WriteLine("Advanced revision sorting finished with {0} errors", nrErrors);
            if (nrErrors == 0 || tolerateErrors)
            {
                if (sortedRevisions == advancedSortedRevisions)
                {
                    logger.WriteLine("Advanced revision sorting has not changed order");
                }
                else
                {
                    logger.WriteLine("Advanced revision sorting was accepted");
                    sortedRevisions = advancedSortedRevisions;
                }
            }
        }

        private ICollection<string> GetPhysicalNamesReferencedBy(SortableRevision revision)
        {
            var names = new List<string>();
            var action = revision.revision.Action;
            switch (action.Type)
            {
                case VssActionType.Label: // VssLabelAction has no physical name
                    break;
                case VssActionType.Create: // VssCreateAction name is the same as item itself
                    break;
                case VssActionType.Destroy: // VssCreateAction has name
                    names.Add(((VssNamedAction)action).Name.PhysicalName);
                    break;
                case VssActionType.Add: // VssAddAction has name
                    names.Add(((VssNamedAction)action).Name.PhysicalName);
                    break;
                case VssActionType.Delete: // VssDeleteAction has name
                    names.Add(((VssNamedAction)action).Name.PhysicalName);
                    break;
                case VssActionType.Recover: // VssRecoverAction has name
                    names.Add(((VssNamedAction)action).Name.PhysicalName);
                    break;
                case VssActionType.Rename: // VssRenameAction has name
                    names.Add(((VssNamedAction)action).Name.PhysicalName);
                    break;
                case VssActionType.MoveFrom: // VssMoveFromAction has name
                    names.Add(((VssNamedAction)action).Name.PhysicalName);
                    break;
                case VssActionType.MoveTo: // VssMoveToAction has name
                    names.Add(((VssNamedAction)action).Name.PhysicalName);
                    break;
                case VssActionType.Share: // ((VssShareAction)action).OriginalProject is not a reference
                    names.Add(((VssNamedAction)action).Name.PhysicalName);
                    break;
                case VssActionType.Pin: // VssPinAction has name
                    names.Add(((VssNamedAction)action).Name.PhysicalName);
                    break;
                case VssActionType.Branch: // VssBranchAction has name and source
                    names.Add(((VssNamedAction)action).Name.PhysicalName);
                    names.Add(((VssBranchAction)action).Source.PhysicalName); // source should better exist, right?
                    break;
                case VssActionType.Edit:
                    names.Add(((VssEditAction)action).PhysicalName);
                    break;
                case VssActionType.Archive: // VssArchiveAction has name; even if we do not use it, it should have existed
                    names.Add(((VssNamedAction)action).Name.PhysicalName);
                    break;
                case VssActionType.Restore: // VssRestoreAction has name for which a create action should exist
                    names.Add(((VssNamedAction)action).Name.PhysicalName);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return names;
        }
    }

    public class OrderingGraph<TItem> where TItem : IComparable<TItem> 
    {
        private class Link : IComparable<Link>
        {
            public readonly Vertex vertex;
            public readonly Dictionary<Link, int> successors = new Dictionary<Link, int>();
            public readonly Dictionary<Link, int> predecessors = new Dictionary<Link, int>();

            public Link(Vertex vertex)
            {
                this.vertex = vertex;
            }

            public int CompareTo(Link other)
            {
                return vertex.CompareTo(other.vertex);
            }

            public bool HasPredecessors
            {
                get { return predecessors.Count > 0; }
            }

            public bool HasSuccessors
            {
                get { return successors.Count > 0; }
            }

            public void AddSuccessor(Link link, int count)
            {
                if (successors.ContainsKey(link))
                {
                    count += successors[link];
                    successors[link] = count;
                    link.predecessors[this] = count;
                }
                else
                {
                    successors[link] = count;
                    link.predecessors[this] = count;
                }

                if (count == 0)
                {
                    RemoveSuccessor(link);
                }
            }

            public void RemoveSuccessor(Link link)
            {
                Debug.Assert(successors.ContainsKey(link));
                Debug.Assert(link.predecessors.ContainsKey(this));
                successors.Remove(link);
                link.predecessors.Remove(this);
            }

            public void Add(Link link, Func<Link, Link> map)
            {
                foreach (var pair in link.successors)
                {
                    AddSuccessor(map(pair.Key), pair.Value);
                }
            }

            public override string ToString()
            {
                return vertex.ToString();
            }
        }

        private class Vertex : IComparable<Vertex>
        {
            public readonly TItem item;

            // specify constraints about order,
            // but not how close in sequence vertices should bee
            public readonly Link order;

            // specify wishes about vertices that should be close in sequence
            public readonly Link buddies;

            public Vertex(TItem item)
            {
                this.item = item;
                order = new Link(this);
                buddies = new Link(this);
            }

            public int CompareTo(Vertex other)
            {
                return item.CompareTo(other.item);
            }

            public override string ToString()
            {
                return string.Format("<{0}-{1}> <{2}-{3}> {4}",
                    order.predecessors.Count,
                    order.successors.Count,
                    buddies.predecessors.Count,
                    buddies.successors.Count,
                    item);
            }
        }

        private readonly Dictionary<TItem, Vertex> vertexOfItem = new Dictionary<TItem, Vertex>();

        private IEnumerable<Vertex> Vertices
        {
            get { return vertexOfItem.Values; }
        }

        public OrderingGraph()
        {
        }

        public OrderingGraph(IEnumerable<TItem> values)
        {
            foreach (var value in values)
            {
                AddNode(value);
            }
        }

        public OrderingGraph(OrderingGraph<TItem> other)
        {
            Dictionary<Vertex, Vertex> map = other.Vertices.ToDictionary(
                v => v,
                v => new Vertex(v.item));

            foreach (var pair in map)
            {
                pair.Value.order.Add(pair.Key.order, link => map[link.vertex].order);
                pair.Value.buddies.Add(pair.Key.buddies, link => map[link.vertex].buddies);
            }

            vertexOfItem = other.vertexOfItem.ToDictionary(
                pair => pair.Key,
                pair => map[pair.Value]);
        }

        public void AddNode(TItem value)
        {
            vertexOfItem.Add(value, new Vertex(value));
        }

        public void AddOrderEdge(TItem predecessor, TItem successor, int count = 1)
        {
            vertexOfItem[predecessor].order.AddSuccessor(vertexOfItem[successor].order, count);
        }

        public void AddBuddyEdge(TItem predecessor, TItem successor, int count = 1)
        {
            vertexOfItem[predecessor].buddies.AddSuccessor(vertexOfItem[successor].buddies, count);
        }

        // Sort items in graph according to the topological constraints,
        // try to keep buddies together
        // und use IComparable of T as final sorting criteria.
        // The graph is kept unchanged (in case a retry with a different strategy is needed).
        public List<TItem> Sort(out int nrErrors)
        {
            var graph = new OrderingGraph<TItem>(this);
            return SortTopologicalThenByBuddiesThenByItemOrder(graph, out nrErrors)
                .Select(v => v.item)
                .ToList();
        }

        private static void VisitInAllDirections(Link link, HashSet<Link> isVisited)
        {
            if (!isVisited.Contains(link))
            {
                isVisited.Add(link);

                foreach (var l in link.successors.Keys)
                {
                    VisitInAllDirections(l, isVisited);
                }
                foreach (var l in link.predecessors.Keys)
                {
                    VisitInAllDirections(l, isVisited);
                }
            }
        }

        private static List<List<Vertex>> GetSortedBuddyGroups(OrderingGraph<TItem> graph)
        {
            var buddyGroups = new List<List<Vertex>>();
            HashSet<Link> isOldVisited = new HashSet<Link>();
            foreach (var vertex in graph.Vertices)
            {
                if (!isOldVisited.Contains(vertex.buddies))
                {
                    HashSet<Link> isNewVisited = new HashSet<Link>();
                    VisitInAllDirections(vertex.buddies, isNewVisited);
                    isOldVisited.UnionWith(isNewVisited);

                    var buddyGroup = isNewVisited.Select(l => l.vertex).ToList();
                    buddyGroup.Sort();
                    buddyGroups.Add(buddyGroup);
                }
            }

            return buddyGroups.OrderBy(g => g.First()).ToList();
        }

        private static List<Vertex> SortTopologicalThenByBuddiesThenByItemOrder(OrderingGraph<TItem> graph,
            out int nrErrors)
        {
            nrErrors = 0;
            if (!graph.Vertices.Any())
            {
                return new List<Vertex>();
            }

            // Approach is somewhat inspired by Kahn's algorithm
            // (see https://en.wikipedia.org/wiki/Topological_sort)
            // where we select the next item for result list using
            // the following strategy.
            // While there are items left to process
            // 1) Choose n as smallest non-blocked items from current buddy group if one exists
            // 2) if no such n exists, we switch to a new buddy group;
            //    if current buddy group is not empty, increase error count because buddy relation is broken;
            //    for the new current buddy group
            //    choose first buddy group if it is not blocked (i.e. no member has predecessors);
            //    else choose a buddy group that unblocks first buddy at least partially;
            //    if this is not possible, try later buddy groups instead of first buddy group;
            //    if all are blocked, choose first buddy group anyway (which will cause errors).
            //    Now choose n as smallest non-blocked item from current buddy group if one exists
            //    or else take first item of new buddy group.
            // 3) If n is blocked, increase number of errors because order relation is broken
            //    and remove predecessor edges.
            // 4) Add n to the resulting sorted list.
            //    Remove n from the current buddy group; remove current buddy group if it is empty.
            //    Remove successor edges of n from the graph.

            var buddyGroups = GetSortedBuddyGroups(graph);
            var buddyGroupOfVertex = new Dictionary<Vertex, List<Vertex>>();
            buddyGroups.ForEach(g => g.ForEach(v => buddyGroupOfVertex[v] = g));

            List<Vertex> currentBuddyGroup = new List<Vertex>();
            List<Vertex> result = new List<Vertex>();

            while (buddyGroups.Count > 0)
            {
                // 1)
                var n = currentBuddyGroup.FirstOrDefault(v => !v.order.HasPredecessors);
                // 2)
                if (n == null)
                {
                    if (currentBuddyGroup.Count > 0)
                    {
                        nrErrors++; // broken buddy relation
                    }

                    // set current buddy group to a good unblocked buddy group
                    // or if that fails fall back to the first buddy group (which will cause errors)
                    currentBuddyGroup =
                        SelectNextBuddyGroupForTopologicalSort(buddyGroups, buddyGroupOfVertex)
                        ?? buddyGroups.First();

                    n = currentBuddyGroup.FirstOrDefault(v => !v.order.HasPredecessors)
                        ?? currentBuddyGroup.First();
                }

                Debug.Assert(currentBuddyGroup != null);
                Debug.Assert(currentBuddyGroup.Count > 0);
                Debug.Assert(n != null);

                // 3)
                if (n.order.HasPredecessors)
                {
                    nrErrors += n.order.predecessors.Count; // broken order constraints
                    n.order.predecessors.Keys.ToList().ForEach(l => l.RemoveSuccessor(n.order));
                }

                Debug.Assert(!n.order.HasPredecessors);

                // 4)
                result.Add(n);
                currentBuddyGroup.Remove(n);
                if (currentBuddyGroup.Count == 0)
                {
                    buddyGroups.Remove(currentBuddyGroup);
                }
                n.order.successors.Keys.ToList().ForEach(l => n.order.RemoveSuccessor(l));
            }

            return result;
        }

        private static List<Vertex> SelectNextBuddyGroupForTopologicalSort(List<List<Vertex>> buddyGroups, Dictionary<Vertex, List<Vertex>> buddyGroupOfVertex)
        {
            // Recursively try to find the first earliest unblocked group that enables the first buddy group.
            // If all failed, return null.
            // Don't check any buddy group twice for blockers.

            List<Vertex> result = null;
            var isChecked = new HashSet<List<Vertex>>();
            var candidates = new List<List<Vertex>> {buddyGroups[0]};
            while (candidates.Count > 0)
            {
                var candidate = candidates[candidates.Count - 1];
                candidates.RemoveAt(candidates.Count - 1);
                if (isChecked.Contains(candidate))
                {
                    continue;
                }

                var blockers = candidate
                    .SelectMany(v => v.order.predecessors.Keys)
                    .Select(l => l.vertex)
                    .Except(candidate) // group internal blockers will be resolved internally or cause an error
                    .Select(v => buddyGroupOfVertex[v])
                    .ToList();
                if (blockers.Count == 0)
                {
                    if (result == null || candidate[0].CompareTo(result[0]) < 0)
                    {
                        result = candidate;
                    }
                }

                isChecked.Add(candidate);
                candidates.AddRange(blockers);
            }

            // If we have not found any group that will work towards unblocking the first group,
            // there still may be unblocked later groups, but they will not unblock
            // the first group, so to preserve order it is better to give up now
            // and handle the problems with the first group.
            return result;
        }
    }
}
