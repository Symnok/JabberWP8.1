using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using JabberWP.Core;
using JabberWP.Services;
using Microsoft.Phone.Scheduler;
using Microsoft.Phone.Shell;

namespace JabberAgent
{
    /// <summary>
    /// Periodic background agent: wakes up, connects, toasts anything waiting, and
    /// goes away again.
    ///
    /// This is the fallback for when the app is not running at all. Continuous
    /// background execution (the location trick in the app) keeps a live connection
    /// while the app stays alive; this covers the case where it has been closed or
    /// evicted.
    ///
    /// Runs under hard limits: the OS schedules it roughly every 30 minutes at its own
    /// discretion, gives it about 25 seconds of wall clock, and caps its memory. So it
    /// connects, drains briefly, and stops - no roster upkeep, no history, no uploads.
    /// Every path must reach NotifyComplete() or the agent is considered faulty and
    /// the OS eventually stops scheduling it.
    /// </summary>
    public class ScheduledAgent : ScheduledTaskAgent
    {
        /// <summary>
        /// How long to stay connected waiting for queued stanzas. Deliberately well
        /// inside the ~25 s budget: being killed mid-run counts against the agent.
        /// </summary>
        private const int DRAIN_SECONDS = 12;

        private static volatile bool _classInitialised;

        public ScheduledAgent()
        {
            if (!_classInitialised)
            {
                _classInitialised = true;
                Deployment.Current.Dispatcher.BeginInvoke(delegate
                {
                    Application.Current.UnhandledException += UnhandledException;
                });
            }
        }

        private void UnhandledException(object sender,
            System.Windows.ApplicationUnhandledExceptionEventArgs e)
        {
            // An unhandled exception in an agent looks like a crash to the scheduler.
            // Swallow it and let the run end quietly instead.
            e.Handled = true;
        }

        protected override void OnInvoke(ScheduledTask task)
        {
            // OnInvoke must not block, and NotifyComplete has to be called exactly
            // once however the run ends - hence the continuation rather than an await.
            RunAsync().ContinueWith(delegate
            {
                try
                {
                    NotifyComplete();
                }
                catch (Exception)
                {
                }
            });
        }

        private async Task RunAsync()
        {
            XmppConnection connection = null;
            try
            {
                // Same IsolatedStorageSettings the app writes: the agent runs in its
                // own process but shares the app's isolated storage.
                XmppAccount account = AccountStore.Load();
                if (account == null || !account.IsUsable)
                {
                    return;
                }

                // The app already has a session: do not open a second one.
                //
                // Connecting as the "-bg" resource makes the account go available
                // then unavailable on every run, and clients that collapse presence
                // to the bare JID keep the last stanza - so the account shows as
                // offline while the app is still connected. Nothing about the
                // second session can prevent that broadcast, so the fix is not to
                // make it. The app is connected anyway, so it receives, stores and
                // notifies on its own; there is nothing here left to do.
                if (SessionHeartbeat.IsAppSessionLive())
                {
                    return;
                }

                // A distinct resource, so this connection cannot collide with the
                // foreground app's session if both happen to be up.
                account.Resource = account.Resource + "-bg";

                List<XmppMessage> received = new List<XmppMessage>();
                connection = new XmppConnection(account);
                connection.MessageReceived += delegate(object sender, XmppMessage message)
                {
                    // Raised on the socket thread. Only collected here; the toasts go
                    // out below, after the drain, so a burst of messages does not turn
                    // into a burst of notifications mid-run.
                    lock (received)
                    {
                        received.Add(message);
                    }
                };

                string error = await connection.ConnectAsync();
                if (error != null)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(DRAIN_SECONDS));
                await connection.DisconnectAsync();

                lock (received)
                {
                    // Store BEFORE notifying.
                    //
                    // This agent is the only thing that received these stanzas, so
                    // the server now considers them delivered and will not hand
                    // them to the app later. Without this write the toast would be
                    // the only copy that ever existed and the message would be lost
                    // for good the moment it was dismissed.
                    store(received);
                    notify(received);
                }
            }
            catch (Exception)
            {
                // Nothing above this catches, and a throwing agent gets deregistered
                // by the OS after enough failures.
            }
            finally
            {
                if (connection != null)
                {
                    try
                    {
                        connection.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        /// <summary>
        /// Writes the drained messages to the history files, grouped so each
        /// conversation is opened and rewritten once rather than once per message.
        /// </summary>
        private void store(List<XmppMessage> messages)
        {
            if (messages.Count == 0)
            {
                return;
            }

            Dictionary<string, List<XmppMessage>> byJid =
                new Dictionary<string, List<XmppMessage>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < messages.Count; i++)
            {
                XmppMessage message = messages[i];
                if (message == null || string.IsNullOrEmpty(message.ContactJid))
                {
                    continue;
                }
                List<XmppMessage> list;
                if (!byJid.TryGetValue(message.ContactJid, out list))
                {
                    list = new List<XmppMessage>();
                    byJid[message.ContactJid] = list;
                }
                list.Add(message);
            }

            foreach (KeyValuePair<string, List<XmppMessage>> pair in byJid)
            {
                try
                {
                    MessageStore.AppendRange(pair.Key, pair.Value);
                }
                catch (Exception)
                {
                    // One unwritable conversation must not cost the others their
                    // messages, nor stop the toasts going out.
                }
            }
        }

        /// <summary>
        /// One toast per sender. Per sender rather than per message so catching up on
        /// a busy chat does not produce a wall of toasts.
        /// </summary>
        private void notify(List<XmppMessage> messages)
        {
            if (messages.Count == 0)
            {
                return;
            }

            Dictionary<string, XmppMessage> latestBySender =
                new Dictionary<string, XmppMessage>(StringComparer.OrdinalIgnoreCase);
            foreach (XmppMessage message in messages)
            {
                if (!string.IsNullOrEmpty(message.ContactJid))
                {
                    latestBySender[message.ContactJid] = message;
                }
            }

            foreach (KeyValuePair<string, XmppMessage> entry in latestBySender)
            {
                try
                {
                    ShellToast toast = new ShellToast();
                    toast.Title = entry.Key;
                    toast.Content = entry.Value.Body ?? "";
                    toast.NavigationUri = new Uri(
                        "/Pages/ChatPage.xaml?chat=" + Uri.EscapeDataString(entry.Key),
                        UriKind.Relative);
                    toast.Show();
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
