using System;
using Microsoft.Phone.Scheduler;

namespace JabberWP.Services
{
    /// <summary>
    /// Registers the periodic agent that checks for messages while the app is not
    /// running.
    ///
    /// Must be re-registered every time the app runs. A PeriodicTask expires after at
    /// most 14 days and the OS simply stops running it - re-adding it on each launch
    /// is the documented way to keep it alive, not a workaround.
    /// </summary>
    public static class BackgroundAgentHelper
    {
        /// <summary>Must match the Name in WMAppManifest's BackgroundServiceAgent.</summary>
        public const string TASK_NAME = "JabberWPAgent";

        /// <summary>Result of the last attempt, for the status line.</summary>
        public static string Status { get; private set; }

        /// <summary>
        /// (Re)registers the agent. Returns null on success or a message explaining
        /// why not - the common reasons are the user's, not ours, and worth showing.
        /// </summary>
        public static string Register()
        {
            try
            {
                // Removing first is required: Add throws if a task with this name is
                // already registered, and there is no update operation.
                PeriodicTask existing = ScheduledActionService.Find(TASK_NAME) as PeriodicTask;
                if (existing != null)
                {
                    ScheduledActionService.Remove(TASK_NAME);
                }

                PeriodicTask task = new PeriodicTask(TASK_NAME);
                task.Description = "Checks for new Jabber messages.";
                // The ceiling the platform allows. Pushed out again on every launch.
                task.ExpirationTime = DateTime.Now.AddDays(14);

                ScheduledActionService.Add(task);

#if DEBUG
                // Debug only: makes the agent run in 30 seconds instead of waiting
                // for the OS to get round to it, which can take the full interval.
                ScheduledActionService.LaunchForTest(TASK_NAME, TimeSpan.FromSeconds(30));
#endif

                Status = "registered";
                return null;
            }
            catch (InvalidOperationException ex)
            {
                // Two distinct user-side causes, and the message is the only way to
                // tell them apart.
                if (ex.Message.Contains("BNS Error: The action is disabled"))
                {
                    Status = "disabled by user";
                    return "Background tasks are turned off for this app. " +
                           "Settings > battery saver > usage > JabberWP.";
                }
                if (ex.Message.Contains("BNS Error: The maximum number of ScheduledActions of this type have already been added"))
                {
                    Status = "too many agents";
                    return "The phone is already running its maximum number of " +
                           "background agents. Turn one off to make room for this one.";
                }

                Status = "failed";
                return "Could not register the background agent: " + ex.Message;
            }
            catch (SchedulerServiceException)
            {
                // Undocumented failure; the platform gives nothing useful back.
                Status = "failed";
                return "Could not register the background agent.";
            }
            catch (Exception ex)
            {
                Status = "failed";
                return "Could not register the background agent: " + ex.Message;
            }
        }

        public static void Unregister()
        {
            try
            {
                if (ScheduledActionService.Find(TASK_NAME) != null)
                {
                    ScheduledActionService.Remove(TASK_NAME);
                }
                Status = "not registered";
            }
            catch (Exception)
            {
            }
        }
    }
}
