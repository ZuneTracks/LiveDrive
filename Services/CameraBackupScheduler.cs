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
        private readonly CameraBackupStateStore _state;

        public CameraBackupScheduler(CameraBackupStateStore state)
        {
            _state = state;
        }

        public bool IsEnabled => _state.IsScheduledBackupEnabled;
        public bool IsRegistered => BackgroundTaskRegistration.AllTasks.Values.Any(task => task.Name == TaskName);

        public async Task<ApplicationTriggerResult> EnableAsync()
        {
            _state.SetScheduledBackupEnabled(true);
            try
            {
                return await EnsureRegisteredAsync(true);
            }
            catch
            {
                _state.SetScheduledBackupEnabled(false);
                throw;
            }
        }

        public async Task EnsureRegisteredAsync()
        {
            if (!_state.IsScheduledBackupEnabled && IsRegistered)
            {
                _state.SetScheduledBackupEnabled(true);
            }

            if (_state.IsScheduledBackupEnabled)
            {
                await EnsureRegisteredAsync(false);
            }
        }

        private async Task<ApplicationTriggerResult> EnsureRegisteredAsync(bool requestInitialRun)
        {
            var access = await BackgroundExecutionManager.RequestAccessAsync();
            if (access != BackgroundAccessStatus.AlwaysAllowed &&
                access != BackgroundAccessStatus.AllowedSubjectToSystemPolicy)
            {
                throw new InvalidOperationException("Windows did not allow scheduled camera backup.");
            }

            if (!IsRegistered)
            {
                UnregisterTask(TaskName, false);
                RegisterTask(TaskName, new TimeTrigger(IntervalMinutes, false));
            }

            if (!requestInitialRun)
            {
                return ApplicationTriggerResult.Allowed;
            }

            UnregisterTask(InitialTaskName, true);
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
            _state.SetScheduledBackupEnabled(false);
            UnregisterTasks(false);
        }

        private static void UnregisterTask(string name, bool cancelRunning)
        {
            foreach (var task in BackgroundTaskRegistration.AllTasks.Values.Where(task => task.Name == name))
            {
                task.Unregister(cancelRunning);
            }
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
