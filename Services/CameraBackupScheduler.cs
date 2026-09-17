using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;

namespace LiveDrive.Services
{
    public sealed class CameraBackupScheduler
    {
        public const string TaskName = "LiveDriveCameraBackup";
        public const string TaskEntryPoint = "LiveDrive.BackgroundTasks.CameraBackupBackgroundTask";
        private const uint IntervalMinutes = 15;

        public bool IsEnabled => BackgroundTaskRegistration.AllTasks.Values.Any(task => task.Name == TaskName);

        public async Task EnableAsync()
        {
            var access = await BackgroundExecutionManager.RequestAccessAsync();
            if (access != BackgroundAccessStatus.AlwaysAllowed &&
                access != BackgroundAccessStatus.AllowedSubjectToSystemPolicy)
            {
                throw new InvalidOperationException("Windows did not allow scheduled camera backup.");
            }

            Disable();
            var builder = new BackgroundTaskBuilder
            {
                Name = TaskName,
                TaskEntryPoint = TaskEntryPoint
            };
            builder.SetTrigger(new TimeTrigger(IntervalMinutes, false));
            builder.Register();
        }

        public void Disable()
        {
            foreach (var task in BackgroundTaskRegistration.AllTasks.Values.Where(task => task.Name == TaskName))
            {
                task.Unregister(false);
            }
        }
    }
}
