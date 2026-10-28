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
        private static readonly TimeSpan SilentActivationThreshold = TimeSpan.FromHours(2);
        private static readonly TimeSpan RegistrationRepairInterval = TimeSpan.FromHours(6);
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
            else if (!requestInitialRun && ShouldRepairSilentRegistration())
            {
                var lastActivation = _state.GetLastBackgroundActivation();
                UnregisterTask(TaskName, false);
                RegisterTask(TaskName, new TimeTrigger(IntervalMinutes, false));
                _state.SaveLastScheduledRegistrationRepair(DateTimeOffset.Now);
                _state.SaveLastResult(
                    "Scheduled Camera Roll backup was re-registered after no background activation for over two hours.");
                await _state.AppendDiagnosticAsync(
                    "Scheduled backup registration repaired",
                    "No background activation since " + lastActivation.Value.ToLocalTime().ToString("g") + ".");
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

        private bool ShouldRepairSilentRegistration()
        {
            var lastActivation = _state.GetLastBackgroundActivation();
            if (!lastActivation.HasValue ||
                DateTimeOffset.Now - lastActivation.Value.ToLocalTime() <= SilentActivationThreshold)
            {
                return false;
            }

            var lastRepair = _state.GetLastScheduledRegistrationRepair();
            return !lastRepair.HasValue ||
                DateTimeOffset.Now - lastRepair.Value.ToLocalTime() >= RegistrationRepairInterval;
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
