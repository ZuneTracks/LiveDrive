using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;

namespace LiveDrive.Services
{
    public sealed class CameraBackupScheduler
    {
        public const string TaskName = "LiveDriveCameraBackup";
        private const string InitialTaskName = "LiveDriveCameraBackupInitial";
        public const string TaskEntryPoint = "LiveDrive.BackgroundTasks.CameraBackupBackgroundTask";
        private const uint IntervalMinutes = 15;

        public bool IsEnabled => BackgroundTaskRegistration.AllTasks.Values.Any(task => task.Name == TaskName);

        public async Task<ApplicationTriggerResult> EnableAsync()
        {
            var access = await BackgroundExecutionManager.RequestAccessAsync();
            if (access != BackgroundAccessStatus.AlwaysAllowed &&
                access != BackgroundAccessStatus.AllowedSubjectToSystemPolicy)
            {
                throw new InvalidOperationException("Windows did not allow scheduled camera backup.");
            }

            UnregisterTasks(true);
            RegisterTask(TaskName, new TimeTrigger(IntervalMinutes, false));

            var initialTrigger = new ApplicationTrigger();
            RegisterTask(InitialTaskName, initialTrigger);
            return await initialTrigger.RequestAsync();
        }

        private static void RegisterTask(string name, IBackgroundTrigger trigger)
        {
            var builder = new BackgroundTaskBuilder
            {
                Name = name,
                TaskEntryPoint = TaskEntryPoint
            };
            builder.SetTrigger(trigger);
            builder.Register();
        }

        public void Disable()
        {
            UnregisterTasks(false);
        }

        private static void UnregisterTasks(bool cancelRunning)
        {
            foreach (var task in BackgroundTaskRegistration.AllTasks.Values.Where(task =>
                task.Name == TaskName || task.Name == InitialTaskName))
            {
                task.Unregister(cancelRunning);
            }
        }
    }
}
