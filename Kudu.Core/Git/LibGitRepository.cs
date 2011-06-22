﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;

namespace Kudu.Core.Git {
    /// <summary>
    /// Implementation of a git repository over LibGit2Sharp
    /// </summary>
    public class LibGitRepository : IRepository {
        private const string GitDir = ".git";

        private Repository _repository;

        public LibGitRepository(string workingDirectory) {
            if (workingDirectory == null) {
                throw new ArgumentNullException("workingDirectory");
            }

            WorkingDirectory = workingDirectory;
        }

        public string CurrentId {
            get {
                return Repository.Head.Tip.Sha;
            }
        }

        private string WorkingDirectory { get; set; }

        private string RepositoryPath {
            get {
                return Path.Combine(WorkingDirectory, GitDir);
            }
        }

        private Repository Repository {
            get {
                if (_repository == null) {
                    _repository = new Repository(RepositoryPath);
                }
                return _repository;
            }
        }

        public void Initialize() {
            // libgit2 throws when trying to reinitialize a git repository so skip it if it exists.
            if (!Directory.Exists(RepositoryPath)) {
                Directory.CreateDirectory(WorkingDirectory);
                // REVIEW: Shold we make the directory hidden like git exe does?
                Repository.Init(WorkingDirectory);
                _repository = null;
            }
        }

        public IEnumerable<FileStatus> GetStatus() {
            throw new NotImplementedException();
        }

        public void Update(string id) {
            throw new NotImplementedException();
        }

        public IEnumerable<ChangeSet> GetChanges() {
            return Repository.Commits.Select(CreateCommit);
        }

        public ChangeSet Commit(string authorName, string message) {
            // REVIEW: Should we loop over all files and stage unstaged ones?
            var signature = new Signature(authorName, authorName, DateTimeOffset.UtcNow);
            Commit commit = Repository.Commit(signature, signature, message);

            return CreateCommit(commit);
        }

        public void AddFile(string path) {
            Repository.Index.Stage(path);
        }
        
        public void RemoveFile(string path) {
            Repository.Index.Unstage(path);
        }

        private static ChangeSet CreateCommit(Commit commit) {
            return new ChangeSet(commit.Id.Sha, 
                                 commit.Author.Name, 
                                 commit.Message, 
                                 commit.Author.When);
        }
    }
}
