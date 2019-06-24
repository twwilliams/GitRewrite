﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CommandLine;
using GitRewrite.Delete;
using GitRewrite.GitObjects;
using GitRewrite.IO;
using Commit = GitRewrite.GitObjects.Commit;
using Tree = GitRewrite.GitObjects.Tree;

namespace GitRewrite
{
    public class Program
    {
        static void Main(string[] args)
        {
            var parserResult = Parser.Default.ParseArguments<CommandLineOptions>(args);
            parserResult.WithParsed(options =>
            {
                var optionsSet = 0;
                optionsSet += options.FixTrees ? 1 : 0;
                optionsSet += options.FilesToDelete.Any() ? 1 : 0;
                optionsSet += options.RemoveEmptyCommits ? 1 : 0;

                if (optionsSet != 1)
                {
                    Console.WriteLine("Cannot mix operations. Only choose one operation at a time (multiple file deletes are allowed).");
                    Console.WriteLine("For available options see GitRewrite --help");
                    return;
                }

                if (options.FixTrees)
                {
                    var defectiveCommits = FindCommitsWithDuplicateTreeEntries(options.RepositoryPath).ToList();

                    var rewrittenCommits = FixDefectiveCommits(options.RepositoryPath, defectiveCommits);
                    if (rewrittenCommits.Any())
                        Refs.Update(options.RepositoryPath, rewrittenCommits);
                }
                else if (options.FilesToDelete.Any())
                {
                    DeleteFiles.Run(options.RepositoryPath, options.FilesToDelete);
                }
                else if (options.RemoveEmptyCommits)
                {
                    var rewrittenCommits = RemoveEmptyCommits(options.RepositoryPath);
                    if (rewrittenCommits.Any())
                        Refs.Update(options.RepositoryPath, rewrittenCommits);
                }
            });
        }

        public static ObjectHash WriteFixedTree(string vcsPath, Tree tree)
        {
            var resultingTreeLines = new List<Tree.TreeLine>();

            bool fixRequired = false;

            foreach (var treeLine in tree.Lines)
            {
                if (!treeLine.IsDirectory())
                {
                    resultingTreeLines.Add(treeLine);
                    continue;
                }

                var childTree = GitObjectFactory.ReadTree(vcsPath, treeLine.Hash);
                var fixedTreeHash = WriteFixedTree(vcsPath, childTree);
                resultingTreeLines.Add(new Tree.TreeLine(treeLine.TextBytes, fixedTreeHash));
                if (fixedTreeHash != childTree.Hash)
                    fixRequired = true;
            }

            if (fixRequired || Tree.HasDuplicateLines(resultingTreeLines))
            {
                tree = Tree.GetFixedTree(resultingTreeLines);
                HashContent.WriteObject(vcsPath, tree);
            }

            return tree.Hash;
        }

        private static bool HasDefectiveTree(string vcsPath, Commit commit)
        {
            if (SeenTrees.TryGetValue(commit.TreeHash, out bool isDefective))
                return isDefective;

            var tree = GitObjectFactory.ReadTree(vcsPath, commit.TreeHash);
            return IsDefectiveTree(vcsPath, tree);
        }

        private static readonly ConcurrentDictionary<ObjectHash, bool> SeenTrees = new ConcurrentDictionary<ObjectHash, bool>();

        public static bool IsDefectiveTree(string vcsPath, Tree tree)
        {
            if (SeenTrees.TryGetValue(tree.Hash, out bool isDefective))
                return isDefective;

            if (Tree.HasDuplicateLines(tree.Lines))
            {
                SeenTrees.TryAdd(tree.Hash, true);
                return true;
            }

            var childTrees = tree.GetDirectories();
            foreach (var childTree in childTrees)
            {
                if (SeenTrees.TryGetValue(childTree.Hash, out isDefective))
                {
                    if (isDefective)
                        return true;

                    continue;
                }

                var childTreeObject = (Tree) GitObjectFactory.ReadGitObject(vcsPath, childTree.Hash);
                if (IsDefectiveTree(vcsPath, childTreeObject))
                { 
                    return true;
                }
            }

            SeenTrees.TryAdd(tree.Hash, false);
            return false;
        }

        private static IEnumerable<ObjectHash> CorrectParents(IEnumerable<ObjectHash> oldParents, Dictionary<ObjectHash, ObjectHash> rewrittenCommitHashes)
        {
            foreach (var oldParentHash in oldParents)
            {
                if (rewrittenCommitHashes.TryGetValue(oldParentHash, out var newParentHash))
                    yield return newParentHash;
                else
                    yield return oldParentHash;
            }
        }
        
        static IEnumerable<ObjectHash> FindCommitsWithDuplicateTreeEntries(string vcsPath)
        {
            foreach (var commit in CommitWalker
                .CommitsRandomOrder(vcsPath)
                .AsParallel()
                .AsUnordered()
                .Select(commit => (commit.Hash, Defective: HasDefectiveTree(vcsPath, commit))))
            {
                if (commit.Defective)
                    yield return commit.Hash;
            }
        }

        private static Dictionary<ObjectHash, ObjectHash> RemoveEmptyCommits(string vcsPath)
        {
            var rewrittenCommitHashes = new Dictionary<ObjectHash, ObjectHash>();
            var commitsWithTreeHashes = new Dictionary<ObjectHash, ObjectHash>();

            foreach (var commit in CommitWalker.CommitsInOrder(vcsPath))
            {
                if (rewrittenCommitHashes.ContainsKey(commit.Hash))
                    continue;

                if (commit.Parents.Count() == 1)
                {
                    var parentHash = Hash.GetRewrittenParentHash(commit, rewrittenCommitHashes);
                    var parentTreeHash = commitsWithTreeHashes[parentHash];
                    if (parentTreeHash == commit.TreeHash)
                    {
                        // This commit will be removed
                        rewrittenCommitHashes.Add(commit.Hash, parentHash);
                        continue;
                    }
                }

                // rewrite this commit
                var correctParents = Hash.GetRewrittenParentHashes(commit.Parents, rewrittenCommitHashes).ToList();
                byte[] newCommitBytes;
                if (correctParents.SequenceEqual(commit.Parents))
                    newCommitBytes = commit.SerializeToBytes();
                else
                    newCommitBytes = Commit.GetSerializedCommitWithChangedTreeAndParents(commit, commit.TreeHash,
                        correctParents);

                var resultBytes = GitObjectFactory.GetBytesWithHeader(GitObjectType.Commit, newCommitBytes);

                var newCommitHash = new ObjectHash(Hash.Create(resultBytes));
                var newCommit = new Commit(newCommitHash, newCommitBytes);

                commitsWithTreeHashes.TryAdd(newCommitHash, newCommit.TreeHash);

                if (newCommitHash != commit.Hash && !rewrittenCommitHashes.ContainsKey(commit.Hash))
                {
                    HashContent.WriteFile(vcsPath, resultBytes, newCommitHash.ToString());
                    rewrittenCommitHashes.Add(commit.Hash, newCommitHash);
                }
            }

            return rewrittenCommitHashes;
        }

        static Dictionary<ObjectHash, ObjectHash> FixDefectiveCommits(string vcsPath, List<ObjectHash> defectiveCommits)
        {
            var rewrittenCommitHashes = new Dictionary<ObjectHash, ObjectHash>();

            foreach (var commit in CommitWalker.CommitsInOrder(vcsPath))
            {
                if (rewrittenCommitHashes.ContainsKey(commit.Hash))
                    continue;

                // Rewrite this commit
                byte[] newCommitBytes;
                if (defectiveCommits.Contains(commit.Hash))
                {
                    var fixedTreeHash = WriteFixedTree(vcsPath, GitObjectFactory.ReadTree(vcsPath, commit.TreeHash));
                    newCommitBytes = Commit.GetSerializedCommitWithChangedTreeAndParents(commit, fixedTreeHash,
                        CorrectParents(commit.Parents, rewrittenCommitHashes).ToList());
                }
                else
                {
                    newCommitBytes = Commit.GetSerializedCommitWithChangedTreeAndParents(commit, commit.TreeHash,
                        CorrectParents(commit.Parents, rewrittenCommitHashes).ToList());
                }

                var fileObjectBytes = GitObjectFactory.GetBytesWithHeader(GitObjectType.Commit, newCommitBytes);
                var newCommitHash = new ObjectHash(Hash.Create(fileObjectBytes));
                if (newCommitHash != commit.Hash && !rewrittenCommitHashes.ContainsKey(commit.Hash))
                {
                    HashContent.WriteFile(vcsPath, fileObjectBytes, newCommitHash.ToString());
                    rewrittenCommitHashes.Add(commit.Hash, newCommitHash);
                }
            }

            return rewrittenCommitHashes;
        }
    }
}
