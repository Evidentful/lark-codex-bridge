using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace LarkCodex {
    internal sealed class LocalProject {
        internal string Id;
        internal string Folder;
        internal string Name;
        internal string Label;
        public override string ToString() { return Label; }
    }

    internal static class ProjectChoices {
        internal static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LarkCodex");
        internal static readonly string Worktrees = Path.Combine(AppData, "worktrees");
        internal static string CodexHome {
            get {
                string home = Environment.GetEnvironmentVariable("CODEX_HOME");
                return string.IsNullOrEmpty(home) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : home;
            }
        }
        private static string Preference { get { return Path.Combine(AppData, "double-click-project.txt"); } }
        private static string TrustPreference { get { return Path.Combine(AppData, "trust-generated-worktrees"); } }
        internal static bool GrantEnabled { get { return File.Exists(TrustPreference); } }

        internal static List<LocalProject> Read() {
            var items = new List<LocalProject> { new LocalProject { Label = "Current chat folder" } };
            try {
                var state = ReadState();
                object value;
                if (state == null || !state.TryGetValue("local-projects", out value)) return items;
                var projects = value as Dictionary<string, object>;
                if (projects == null) return items;
                foreach (var project in projects) {
                    var entry = project.Value as Dictionary<string, object>;
                    object name, paths;
                    if (entry == null || !entry.TryGetValue("name", out name) || !entry.TryGetValue("rootPaths", out paths)) continue;
                    foreach (var folder in (paths as object[] ?? new object[0]).OfType<string>()) {
                        if (!GitWorktrees.IsLocalPath(folder) || !Directory.Exists(folder) || GitWorktrees.Contains(Worktrees, folder)) continue;
                        if (items.Any(p => string.Equals(p.Folder, folder, StringComparison.OrdinalIgnoreCase))) continue;
                        string display = name as string ?? Path.GetFileName(folder);
                        items.Add(new LocalProject { Id = project.Key, Folder = folder, Name = display, Label = display + " — " + folder });
                    }
                }
            } catch (Exception e) {
                Log.Write("Codex project list unavailable: " + e.GetType().Name);
            }
            return items;
        }

        private static Dictionary<string, object> ReadState() {
            var json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
            return json.DeserializeObject(File.ReadAllText(Path.Combine(CodexHome, ".codex-global-state.json"))) as Dictionary<string, object>;
        }

        internal static bool HasSidebarMembership(string chatId, string projectId) {
            try {
                object assignments, member, assignedProject, kind;
                var state = ReadState();
                return state != null && state.TryGetValue("thread-project-assignments", out assignments) &&
                    assignments is Dictionary<string, object> && ((Dictionary<string, object>)assignments).TryGetValue(chatId, out member) &&
                    member is Dictionary<string, object> && ((Dictionary<string, object>)member).TryGetValue("projectId", out assignedProject) &&
                    ((Dictionary<string, object>)member).TryGetValue("projectKind", out kind) && Equals(assignedProject, projectId) && Equals(kind, "local");
            } catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static string ResolveBlankComposer(string label) {
            string managed = null;
            if (!string.IsNullOrEmpty(label) && label.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                System.Text.RegularExpressions.Regex.IsMatch(label, @"-branch-[0-9]+-[0-9a-f]{8}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) {
                string folder = Path.Combine(Worktrees, label);
                if (File.Exists(Path.Combine(folder, ".git"))) managed = folder;
            }
            return ResolveUniqueFolder(label, Read(), managed);
        }

        internal static string ResolveUniqueFolder(string label, IEnumerable<LocalProject> projects, string managed) {
            if (string.IsNullOrWhiteSpace(label)) return null;
            var matches = projects.Where(project => project.Folder != null &&
                    (string.Equals(project.Name, label, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(Path.GetFileName(project.Folder.TrimEnd('\\','/')), label, StringComparison.OrdinalIgnoreCase)))
                .Select(project => project.Folder);
            if (managed != null) matches = matches.Concat(new[] {managed});
            var unique = matches.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            return unique.Length == 1 ? unique[0] : null;
        }

        internal static string Load() {
            try { return File.ReadAllText(Preference).Trim(); }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        internal static void Save(string folder) {
            Directory.CreateDirectory(AppData);
            File.WriteAllText(Preference, folder ?? "");
        }
        internal static void SetGrant(bool enabled) {
            if (!enabled) { File.Delete(TrustPreference); return; }
            Directory.CreateDirectory(AppData);
            File.WriteAllText(TrustPreference, "Trust only LARK-created worktrees from an already trusted local repository.\n");
        }
    }

    internal sealed class WorktreeTarget {
        internal string Path;
        internal string Branch;
        internal string Source;
        internal string ChatId;
        internal LocalProject SidebarProject;
        internal string ChatName { get { return Branch; } }
        internal string Link { get { return "codex://threads/" + ChatId; } }

        internal bool MatchesChatLink(string value) {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "codex" &&
                uri.Host == "threads" && string.Equals(uri.AbsolutePath, "/" + ChatId, StringComparison.OrdinalIgnoreCase);
        }

        internal static string ProjectRecord(string worktrees, string worktree) {
            string folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(worktrees), "worktree-projects");
            return System.IO.Path.Combine(folder, System.IO.Path.GetFileName(worktree) + ".txt");
        }
    }

    internal static class GitWorktrees {
        internal static bool IsLocalPath(string path) {
            return !string.IsNullOrEmpty(path) && path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/') && path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0;
        }

        internal static bool Contains(string root, string path) {
            if (!IsLocalPath(root) || !IsLocalPath(path)) return false;
            string prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return (Path.GetFullPath(path).TrimEnd('\\') + "\\").StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        internal static string NextBranch(string refs) {
            int highest = 0;
            foreach (string branch in refs.Split(new[] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries)) {
                int suffix;
                if (branch.StartsWith("branch-", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(branch.Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out suffix))
                    highest = Math.Max(highest, suffix);
            }
            return "branch-" + checked(highest + 1).ToString(CultureInfo.InvariantCulture);
        }

        // Quote each argument for the Windows process argument parser. Git runs directly.
        private static string Quote(string value) {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char ch in value) {
                if (ch == '\\') { slashes++; continue; }
                result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
                result.Append(ch);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        internal static string Git(string cwd, Func<bool> cancelled, params string[] args) {
            if (cancelled()) throw new OperationCanceledException();
            var start = new ProcessStartInfo("git.exe", string.Join(" ", args.Select(Quote).ToArray())) {
                WorkingDirectory = cwd, CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables.Remove("GIT_DIR");
            start.EnvironmentVariables.Remove("GIT_WORK_TREE");
            start.EnvironmentVariables.Remove("GIT_INDEX_FILE");
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            using (var git = Process.Start(start)) {
                var output = git.StandardOutput.ReadToEndAsync();
                var errors = git.StandardError.ReadToEndAsync();
                var time = Stopwatch.StartNew();
                while (!git.WaitForExit(100)) {
                    if (cancelled() || time.Elapsed.TotalSeconds > 30) {
                        try { git.Kill(); } catch (InvalidOperationException) {}
                        if (cancelled()) throw new OperationCanceledException();
                        throw new IOException("Git timed out. Check the project before retrying.");
                    }
                }
                if (git.ExitCode != 0) throw new IOException("Git could not prepare the worktree. " + errors.GetAwaiter().GetResult().Trim());
                return output.GetAwaiter().GetResult().Trim();
            }
        }

        internal static string ResolveRepository(string folder, Func<bool> cancelled) {
            if (!IsLocalPath(folder) || !Directory.Exists(folder)) throw new IOException("The chat must use a local Windows Git folder.");
            string repo = Git(folder, cancelled, "rev-parse", "--show-toplevel");
            if (!IsLocalPath(repo)) throw new IOException("Git did not return a local repository folder.");
            return Path.GetFullPath(repo);
        }

        internal static string PrimaryCheckout(string repository, Func<bool> cancelled) {
            const string prefix = "worktree ";
            string record = Git(repository, cancelled, "worktree", "list", "--porcelain", "-z").Split('\0')[0];
            if (!record.StartsWith(prefix, StringComparison.Ordinal) || !IsLocalPath(record.Substring(prefix.Length)))
                throw new IOException("Git could not identify the primary project checkout.");
            return Path.GetFullPath(record.Substring(prefix.Length));
        }

        internal static WorktreeTarget Create(string repo, string primary, string parent, Func<bool> cancelled, Action<string> verifySource) {
            verifySource(repo);
            string head = Git(repo, cancelled, "rev-parse", "--verify", "HEAD^{commit}");
            string branch = NextBranch(Git(repo, cancelled, "for-each-ref", "--format=%(refname:short)", "refs/heads"));
            string name = new DirectoryInfo(primary).Name;
            if (name.Length > 42) name = name.Substring(0, 42);
            Directory.CreateDirectory(parent);
            string path = Path.Combine(parent, name + "-" + branch + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Git(repo, cancelled, "-c", "core.hooksPath=/dev/null", "worktree", "add", "-b", branch, "--", path, head);
            string checkedOut = Git(path, cancelled, "symbolic-ref", "--short", "HEAD");
            if (!string.Equals(checkedOut, branch, StringComparison.Ordinal)) throw new IOException("The new branch could not be verified.");
            return new WorktreeTarget { Path = path, Branch = branch, Source = repo };
        }

        internal static void SetCodexOwner(WorktreeTarget target, Func<bool> cancelled) {
            // Codex resolves linked worktrees by their owner before changing sidebar membership.
            string common = Git(target.Path, cancelled, "rev-parse", "--path-format=absolute", "--git-common-dir");
            string owner = Git(target.Path, cancelled, "rev-parse", "--path-format=absolute", "--git-path", "codex-thread.json");
            string metadataRoot = Path.Combine(common, "worktrees");
            string directory = Path.GetDirectoryName(Path.GetFullPath(owner));
            if (!Contains(metadataRoot, directory) || !string.Equals(Path.GetDirectoryName(directory), Path.GetFullPath(metadataRoot), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Git could not verify the worktree's separate metadata directory.");
            using (var file = new FileStream(owner, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                writer.WriteLine(new JavaScriptSerializer().Serialize(new { version = 1, ownerThreadId = target.ChatId }));
        }
    }

    internal sealed class CodexProjectClient : IDisposable {
        private readonly Process server;
        private readonly Func<bool> cancelled;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        private int nextId;

        internal CodexProjectClient(string executable, string codexHome, Func<bool> cancelled) {
            this.cancelled = cancelled;
            var start = new ProcessStartInfo(executable, "app-server --listen stdio://") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false)
            };
            start.EnvironmentVariables["CODEX_HOME"] = codexHome;
            start.EnvironmentVariables.Remove("CODEX_SQLITE_HOME");
            server = Process.Start(start);
            server.BeginErrorReadLine();
            try {
                Call("initialize", new {
                    clientInfo = new { name = "lark_codex", title = "LARK Codex", version = "1.0" },
                    capabilities = new { experimentalApi = true }
                });
                Write(new { method = "initialized" });
            } catch {
                Dispose();
                throw;
            }
        }

        private void Write(object message) {
            server.StandardInput.WriteLine(json.Serialize(message));
            server.StandardInput.Flush();
        }

        internal Dictionary<string, object> Call(string method, object parameters) {
            if (cancelled()) throw new OperationCanceledException();
            int id = ++nextId;
            Write(new { id = id, method = method, @params = parameters });
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds <= 8) {
                Task<string> line = server.StandardOutput.ReadLineAsync();
                while (!line.Wait(100)) {
                    if (cancelled()) throw new OperationCanceledException();
                    if (clock.Elapsed.TotalSeconds > 8) throw new IOException("Codex request timed out: " + method);
                }
                string incoming = line.GetAwaiter().GetResult();
                if (incoming == null) throw new IOException("The Codex app server closed.");
                var reply = json.DeserializeObject(incoming) as Dictionary<string, object>;
                if (reply == null || !object.Equals(Field(reply,"id"),id)) continue;
                var result = Field(reply,"result") as Dictionary<string, object>;
                if (result == null) throw new IOException("Codex rejected " + method + ".");
                return result;
            }
            throw new IOException("Codex request timed out: " + method);
        }

        private static object Field(Dictionary<string, object> source, string key) {
            object value;
            return source != null && source.TryGetValue(key,out value) ? value : null;
        }

        private static IEnumerable<Dictionary<string, object>> Entries(Dictionary<string, object> response, string key) {
            return (Field(response, key) as object[] ?? new object[0]).OfType<Dictionary<string, object>>();
        }

        internal LocalProject ResolveProject(string folder, string repo, string primary, string worktrees) {
            var projects = new List<Dictionary<string, object>>();
            string cursor = null;
            do {
                var page = Call("project/list", new { cursor, limit = 100 });
                projects.AddRange(Entries(page, "data"));
                cursor = Field(page, "nextCursor") as string;
            } while (!string.IsNullOrEmpty(cursor));

            var roots = (from project in projects
                         let id = Field(project, "id") as string
                         where !string.IsNullOrEmpty(id)
                         from root in Entries(project, "roots")
                         let path = Field(root, "path") as string
                         where GitWorktrees.IsLocalPath(path) && !GitWorktrees.Contains(worktrees, path)
                         select new LocalProject { Id = id, Folder = Path.GetFullPath(path) }).ToArray();
            if (GitWorktrees.Contains(worktrees, repo)) {
                // Zero-turn chats are omitted from thread/list. Remember their project
                // outside the checkout, including projects rooted at a Git subfolder.
                string record = WorktreeTarget.ProjectRecord(worktrees, repo);
                if (File.Exists(record)) {
                    string saved = File.ReadAllText(record).Trim();
                    var previous = roots.FirstOrDefault(root => root.Id == saved);
                    if (previous != null) return previous;
                }
                folder = primary;
            }

            var matches = roots.Where(root => GitWorktrees.Contains(root.Folder, folder))
                .GroupBy(root => root.Folder.TrimEnd('\\').Length).OrderByDescending(group => group.Key).FirstOrDefault();
            if (matches != null) {
                var unique = matches.GroupBy(root => root.Id).Take(2).ToArray();
                if (unique.Length == 1) return unique[0].First();
            }
            throw new IOException("Could not identify the parent Codex project. Choose the project in LARK.");
        }

        internal void CreateEmptyChat(WorktreeTarget target, string projectId, string worktrees) {
            var result = Call("thread/start", new {
                cwd = target.Path, projectId, runtimeWorkspaceRoots = new[] { target.Path }, ephemeral = false
            });
            var thread = Field(result, "thread") as Dictionary<string, object>;
            string id = Field(thread, "id") as string;
            Guid parsed;
            if (!Guid.TryParseExact(id, "D", out parsed)) throw new IOException("Codex did not return a chat ID.");
            // Naming persists a zero-turn chat so the desktop app can open it by ID.
            Call("thread/name/set", new { threadId = id, name = target.ChatName });
            target.ChatId = id;
            VerifyEmptyChat(target, projectId);
            GitWorktrees.SetCodexOwner(target, cancelled);
            string record = WorktreeTarget.ProjectRecord(worktrees, target.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(record));
            File.WriteAllText(record, projectId);
        }

        internal void VerifyEmptyChat(WorktreeTarget target, string projectId) {
            var result = Call("thread/read", new { threadId = target.ChatId, includeTurns = true });
            var thread = Field(result, "thread") as Dictionary<string, object>;
            string cwd = Field(thread, "cwd") as string;
            var turns = Field(thread, "turns") as object[];
            if (!GitWorktrees.IsLocalPath(cwd) || !string.Equals(Path.GetFullPath(cwd), target.Path, StringComparison.OrdinalIgnoreCase) ||
                !object.Equals(Field(thread, "projectId"), projectId) || !object.Equals(Field(thread, "name"), target.ChatName) ||
                turns == null || turns.Length != 0)
                throw new IOException("Codex did not confirm the empty chat in the expected project and worktree.");
        }

        private static string TrustLevel(Dictionary<string, object> result, string folder, bool exactCase=false) {
            var config = Field(result,"config") as Dictionary<string, object>;
            var projects = Field(config,"projects") as Dictionary<string, object>;
            if (projects == null) return null;
            string path = Path.GetFullPath(folder).TrimEnd('\\');
            string matched = null;
            foreach (var entry in projects) {
                if (!GitWorktrees.IsLocalPath(entry.Key) ||
                    !string.Equals(Path.GetFullPath(entry.Key).TrimEnd('\\'),path,
                        exactCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) continue;
                string level = Field(entry.Value as Dictionary<string, object>,"trust_level") as string;
                if (level == "untrusted") return level;
                if (level != null) matched = level;
            }
            return matched;
        }

        internal void RequireTrustedSource(string source) {
            var result = Call("config/read",new {includeLayers=false});
            if (TrustLevel(result,source)!="trusted")
                throw new IOException("Open and trust the source repository in Codex once before creating worktrees.");
        }

        internal void Register(WorktreeTarget target, string parent) {
            string path = Path.GetFullPath(target.Path);
            string actualParent = Path.GetDirectoryName(path);
            if (!string.Equals(actualParent,Path.GetFullPath(parent),StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(path) || (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0 ||
                (File.GetAttributes(actualParent)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("The generated worktree path could not be verified.");
            string sourceGit = GitWorktrees.Git(target.Source,cancelled,"rev-parse","--path-format=absolute","--git-common-dir");
            string targetGit = GitWorktrees.Git(path,cancelled,"rev-parse","--path-format=absolute","--git-common-dir");
            string branch = GitWorktrees.Git(path,cancelled,"symbolic-ref","--short","HEAD");
            if (!string.Equals(sourceGit,targetGit,StringComparison.OrdinalIgnoreCase) || branch!=target.Branch)
                throw new IOException("The new folder is not on the expected repository and branch.");
            var result = Call("config/read",new {includeLayers=true});
            if (TrustLevel(result,target.Source)!="trusted") throw new IOException("The source repository is no longer trusted.");
            string trust = TrustLevel(result,path);
            if (trust!=null && trust!="trusted") throw new IOException("This folder has an explicit trust setting. Review it in Codex.");
            if (TrustLevel(result,path,exactCase:true)=="trusted") return;
            var layers = Field(result,"layers") as object[] ?? new object[0];
            string version = null;
            foreach (var layer in layers.OfType<Dictionary<string,object>>()) {
                if (object.Equals(Field(Field(layer,"name") as Dictionary<string,object>,"type"),"user"))
                    version = Field(layer,"version") as string;
            }
            if (string.IsNullOrEmpty(version)) throw new IOException("Codex did not report the user configuration version.");
            // Desktop can look up the working directory by its exact spelling.
            string key = "projects." + json.Serialize(path) + ".trust_level";
            var written = Call("config/batchWrite",new {
                edits = new[] { new {keyPath=key,mergeStrategy="replace",value="trusted"} },
                expectedVersion=version
            });
            if (!object.Equals(Field(written,"status"),"ok") ||
                TrustLevel(Call("config/read",new {includeLayers=false}),path,exactCase:true)!="trusted")
                throw new IOException("Codex could not confirm trust for the worktree.");
        }

        public void Dispose() {
            try {
                server.StandardInput.Close();
                if (!server.WaitForExit(500)) server.Kill();
            } catch (InvalidOperationException) {}
            catch (IOException e) { Log.Write("Closing the Codex app server: " + e.Message); }
            catch (System.ComponentModel.Win32Exception e) { Log.Write("Closing the Codex app server: " + e.Message); }
            finally {server.Dispose();}
        }
    }

    internal static class CurrentChatFolder {
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();

        internal static string Read(CodexView view, Func<bool> cancelled) {
            var folder = view.NewComposer ? ProjectChoices.ResolveBlankComposer(view.ProjectLabel) : null;
            return folder ?? Read(view.Window,cancelled);
        }

        internal static string Read(IntPtr window, Func<bool> cancelled) {
            return ReadClipboard(window,cancelled,DesktopNative.CopyWorkingDirectory,
                text=>GitWorktrees.IsLocalPath(text) && Directory.Exists(text));
        }

        internal static bool IsExpectedChat(IntPtr window, WorktreeTarget target, Func<bool> cancelled) {
            return ReadClipboard(window,cancelled,DesktopNative.CopyChatLink,target.MatchesChatLink)!=null;
        }

        private static string ReadClipboard(IntPtr window, Func<bool> cancelled, Func<IntPtr,bool> copy, Func<string,bool> matches) {
            string value = null;
            var sta = new Thread(delegate() {
                IDataObject previous = null;
                uint copiedSequence = 0;
                try {
                    if (cancelled() || DesktopNative.GetForegroundWindow() != window) return;
                    previous = Clipboard.GetDataObject();
                    uint before = GetClipboardSequenceNumber();
                    if (!copy(window)) return;
                    var clock = Stopwatch.StartNew();
                    while (!cancelled() && clock.ElapsedMilliseconds < 1500) {
                        if (DesktopNative.GetForegroundWindow() != window) return;
                        uint sequence = GetClipboardSequenceNumber();
                        if (sequence != before) {
                            uint writer, app;
                            DesktopNative.GetWindowThreadProcessId(GetClipboardOwner(), out writer);
                            DesktopNative.GetWindowThreadProcessId(window, out app);
                            if (writer != app || app == 0) return;
                            copiedSequence = sequence;
                            var copied = Clipboard.GetText().Trim();
                            if (GetClipboardSequenceNumber() == sequence && matches(copied)) value = copied;
                            return;
                        }
                        Thread.Sleep(30);
                    }
                } catch (Exception e) {
                    Log.Write("Read Codex clipboard: " + e.GetType().Name);
                } finally {
                    if (copiedSequence != 0 && GetClipboardSequenceNumber() == copiedSequence) {
                        try {
                            if (previous == null) Clipboard.Clear();
                            else Clipboard.SetDataObject(previous, true, 2, 50);
                        } catch (Exception e) { Log.Write("Clipboard restore: " + e.GetType().Name); }
                    }
                }
            }) { IsBackground = true };
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            return sta.Join(2500) ? value : null;
        }
    }
}
