using System;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace TiaCli.Openness
{
    /// <summary>
    /// Fills TIA Portal's "Authorized connection" window while tia goes online. On V20 that login is
    /// the one question going online never puts to Openness - a download hears it on its connection,
    /// going online only ever shows TIA's own window - so this watches for the window and types into
    /// it through Windows UI Automation, the way a screen reader drives an application.
    ///
    /// The window is found by what it is rather than by its English title: a window of the portal's
    /// process whose title names the CPU in brackets ("[BakingLine]") and which holds a password
    /// field. Each run fills it once. If TIA shows it again, the PLC refused the login, and the window
    /// is cancelled so the command fails with that reason instead of waiting for a person.
    /// </summary>
    internal sealed class LoginWindowFiller : IDisposable
    {
        private readonly int _processId;
        private readonly string _cpuName;
        private readonly string _user;
        private readonly string _password;
        private readonly string _userType;
        private readonly Thread _thread;
        private volatile bool _stop;

        /// <summary>True once the window was filled and confirmed.</summary>
        public bool Filled { get; private set; }

        /// <summary>Why the login did not get through, when the filler gave up; otherwise null.</summary>
        public string Refused { get; private set; }

        public LoginWindowFiller(int processId, string cpuName, string user, string password, string userType)
        {
            _processId = processId;
            _cpuName = cpuName;
            _user = user;
            _password = password;
            _userType = userType;
            _thread = new Thread(Watch) { IsBackground = true, Name = "login-window" };
            _thread.Start();
        }

        private void Watch()
        {
            Log($"watching for the login window of [{_cpuName}] in process {_processId}");
            while (!_stop)
            {
                try
                {
                    var window = FindLoginWindow();
                    if (window != null)
                    {
                        if (Filled)
                        {
                            // A second window after a filled one is TIA asking again: the login was wrong.
                            Refused = $"The PLC refused the login for user '{_user}': TIA asked again after tia filled in its login window.";
                            Log("login window came back after filling - cancelling it");
                            Press(window, cancel: true);
                            return;
                        }

                        Fill(window);
                        Filled = true;
                        if (!WaitUntilClosed(window, TimeSpan.FromSeconds(5)))
                        {
                            Refused = $"TIA kept its login window open after tia filled it for user '{_user}' - the login was most likely refused.";
                            Log("login window stayed open after filling - cancelling it");
                            Press(window, cancel: true);
                            return;
                        }
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // The window closed while it was being read; look again.
                }
                catch (Exception ex)
                {
                    Log("login window: " + ex.Message);
                }
                Thread.Sleep(300);
            }
        }

        private readonly System.Collections.Generic.HashSet<string> _seen = new System.Collections.Generic.HashSet<string>();

        /// <summary>
        /// Every top-level window of a Siemens process, and the windows directly under them. Not only
        /// the portal's own process: on V20 the filler never saw the login window there, so TIA draws
        /// it from a process of its own. Each window seen is logged once, to show what the dialog is
        /// when it is still missed.
        /// </summary>
        private AutomationElement FindLoginWindow()
        {
            var windows = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window);

            foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                if (!IsSiemens(top)) continue;
                if (IsLoginWindow(top)) return top;
                // A modal dialog can sit under its owner rather than at the top.
                foreach (AutomationElement child in top.FindAll(TreeScope.Children, windows))
                    if (IsLoginWindow(child)) return child;
            }
            return null;
        }

        private bool IsSiemens(AutomationElement element)
        {
            var pid = element.Current.ProcessId;
            if (pid == _processId) return true;
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(pid))
                    return process.ProcessName.StartsWith("Siemens", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private bool IsLoginWindow(AutomationElement window)
        {
            var current = window.Current;
            var name = current.Name ?? "";
            var key = $"{current.ProcessId}|{current.ControlType.ProgrammaticName}|{name}";
            if (_seen.Add(key))
                Log($"window seen: '{name}' ({current.ControlType.ProgrammaticName}, class {current.ClassName}, process {current.ProcessId})");

            // On V20 UI Automation does not report the visible title ("Authorized connection
            // [BakingLine]") but a resource key - 'DialogFrameControl_Siemens.Automation.DomainServices.UI:
            // ...OnlineService.LegitimationDialogResources:HeadingLegitimationEnterPasswordHeader' - in the
            // portal's own process. That key is the same in every language, so it is what is matched;
            // the bracketed title stays for a TIA that reports what it shows.
            if (name.IndexOf("LegitimationDialog", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("[" + _cpuName + "]", StringComparison.OrdinalIgnoreCase) < 0) return false;
            return window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsPasswordProperty, true)) != null;
        }

        private void Fill(AutomationElement window)
        {
            Log($"login window found: '{window.Current.Name}'");

            var combo = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));
            if (combo != null && !string.IsNullOrEmpty(_userType)) ChooseUserType(combo);

            var edits = window.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>()
                .ToList();
            // An editable combo box carries an edit of its own; the fields are the ones outside it.
            var fields = edits.Where(e => !IsInside(e, combo)).ToList();
            Log($"login window fields: {fields.Count} edit(s), {fields.Count(e => e.Current.IsPassword)} marked as password");
            var passwordBox = fields.FirstOrDefault(e => e.Current.IsPassword);
            var userBox = fields.FirstOrDefault(e => !e.Current.IsPassword);
            // A custom field may not say it is a password one: TIA's window has user name, then password.
            if (passwordBox == null && fields.Count >= 2)
            {
                userBox = fields[0];
                passwordBox = fields[1];
            }

            if (userBox != null && !string.IsNullOrEmpty(_user))
            {
                SetText(userBox, _user);
                Log("user name filled");
            }
            if (passwordBox == null) throw new InvalidOperationException("the login window has no password field after all");
            SetText(passwordBox, _password ?? "");
            Log("password filled");

            Press(window, cancel: false);
            Log("login window confirmed");
        }

        /// <summary>The kinds in TIA's list, matched in English and German: the window is localised.</summary>
        private void ChooseUserType(AutomationElement combo)
        {
            string[] words;
            switch (_userType.Trim().ToLowerInvariant())
            {
                case "local":
                case "project": words = new[] { "local", "lokal", "project", "projekt" }; break;
                case "global": words = new[] { "global", "zentral" }; break;
                case "anonymous": words = new[] { "anonym" }; break;
                case "password": words = new[] { "password", "kennwort", "passwort" }; break;
                case "sso": words = new[] { "single sign", "sso" }; break;
                default: return;
            }

            if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
                ((ExpandCollapsePattern)expand).Expand();
            try
            {
                var items = combo.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                foreach (AutomationElement item in items)
                {
                    var text = (item.Current.Name ?? "").ToLowerInvariant();
                    if (!words.Any(text.Contains)) continue;
                    if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
                    {
                        ((SelectionItemPattern)select).Select();
                        Log($"user type chosen: '{item.Current.Name}'");
                        return;
                    }
                }
                Log($"user type '{_userType}' not found in the window's list; left as it was");
            }
            finally
            {
                if (expand != null)
                    try { ((ExpandCollapsePattern)expand).Collapse(); } catch { }
            }
        }

        private static bool IsInside(AutomationElement element, AutomationElement container)
        {
            if (container == null) return false;
            var walker = TreeWalker.ControlViewWalker;
            for (var parent = walker.GetParent(element); parent != null; parent = walker.GetParent(parent))
                if (Automation.Compare(parent, container)) return true;
            return false;
        }

        private static void SetText(AutomationElement box, string text)
        {
            if (box.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            {
                ((ValuePattern)value).SetValue(text);
                return;
            }

            // Some password fields offer no value to set: type it instead, which needs the focus.
            box.SetFocus();
            System.Windows.Forms.SendKeys.SendWait(EscapeForSendKeys(text));
        }

        /// <summary>SendKeys reads + ^ % ~ ( ) { } [ ] as commands; braces make them literal.</summary>
        private static string EscapeForSendKeys(string text)
        {
            var escaped = new System.Text.StringBuilder();
            foreach (var c in text)
            {
                if ("+^%~(){}[]".IndexOf(c) >= 0) escaped.Append('{').Append(c).Append('}');
                else escaped.Append(c);
            }
            return escaped.ToString();
        }

        /// <summary>
        /// Presses OK or Cancel. Recognised by name in English and German, then by position: the
        /// confirming button comes first in TIA's dialogs.
        /// </summary>
        private static void Press(AutomationElement window, bool cancel)
        {
            var buttons = window.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>()
                .ToList();

            string[] names = cancel ? new[] { "cancel", "abbrechen" } : new[] { "ok" };
            var button = buttons.FirstOrDefault(b => names.Contains((b.Current.Name ?? "").Trim().ToLowerInvariant()))
                         ?? buttons.FirstOrDefault(b => names.Any(n => (b.Current.AutomationId ?? "").ToLowerInvariant().Contains(n)))
                         ?? (cancel ? buttons.LastOrDefault() : buttons.FirstOrDefault());

            if (button != null && button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                ((InvokePattern)invoke).Invoke();
                return;
            }
            if (cancel && window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
            {
                ((WindowPattern)pattern).Close();
                return;
            }
            throw new InvalidOperationException($"no {(cancel ? "Cancel" : "OK")} button found in the login window");
        }

        private static bool WaitUntilClosed(AutomationElement window, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                try
                {
                    // Reading a property of a closed window throws; an offscreen one is on its way out.
                    if (window.Current.IsOffscreen) return true;
                }
                catch (ElementNotAvailableException)
                {
                    return true;
                }
                Thread.Sleep(200);
            }
            return false;
        }

        private static void Log(string message) => TiaCli.Daemon.DaemonPaths.Log(message);

        public void Dispose()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
