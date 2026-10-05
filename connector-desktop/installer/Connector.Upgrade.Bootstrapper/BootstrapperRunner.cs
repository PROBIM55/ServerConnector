using Connector.Upgrade.Core;

namespace Connector.Upgrade.Bootstrapper;

public sealed class BootstrapperRunner(
    IWindowsUpgradeBootstrapperRuntimeFactory runtimeFactory,
    IPlatformTokenPrompt tokenPrompt,
    IBootstrapperStatusSink status)
{
    private readonly IWindowsUpgradeBootstrapperRuntimeFactory _runtimeFactory = runtimeFactory;
    private readonly IPlatformTokenPrompt _tokenPrompt = tokenPrompt;
    private readonly IBootstrapperStatusSink _status = status;

    public static int ToProcessExitCode(UpgradeExecutionResult? result) => result switch
    {
        { Outcome: UpgradeOutcome.Succeeded or UpgradeOutcome.AlreadyCommitted, RebootRequired: true } => 3010,
        { Outcome: UpgradeOutcome.Succeeded or UpgradeOutcome.AlreadyCommitted } => 0,
        null => 1602,
        _ => 1,
    };

    public async Task<UpgradeExecutionResult?> RunAsync(BootstrapperAction action, CancellationToken cancellationToken = default)
    {
        BootstrapperPreparation preparation;
        try
        {
            _status.Show("Проверка безопасной среды и подготовка подключения…");
            preparation = await _runtimeFactory.PrepareAsync(action, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _status.Show("Операция отменена.");
            return null;
        }
        catch
        {
            _status.Show("Не удалось подготовить безопасное подключение. Ничего не изменено.");
            return null;
        }

        using var runGuard = preparation.RunGuard;
        if (!preparation.IsReady || preparation.Composition is null)
        {
            preparation.RunGuard?.Dispose();
            foreach (var blocker in preparation.Blockers)
                _status.Show(BlockerMessage(blocker.Code));
            if (preparation.Blockers.Count == 0)
                _status.Show("Безопасная композиция недоступна. Ничего не изменено.");
            preparation.Composition?.Dispose();
            return null;
        }

        using var composition = preparation.Composition;
        try
        {
            UpgradeExecutionResult result;
            if (action == BootstrapperAction.RecoverInterrupted)
            {
                _status.Show("Проверка и восстановление прерванной операции…");
                result = await composition.RecoverInterruptedAsync(cancellationToken).ConfigureAwait(true);
            }
            else
            {
                // The original-user composition performs its own journaled upgrade preflight
                // before any mutation. Ask for the one-use credential only after composition
                // preparation has passed its read-only checks.
                var value = await _tokenPrompt.RequestTokenAsync(cancellationToken).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(value))
                {
                    _status.Show("Операция отменена. Токен не использован.");
                    return null;
                }

                _status.Show("Проверка системы и выполнение переноса доступа…");
                try
                {
                    result = await composition.ExecuteAsync(new OneTimePlatformToken(value), cancellationToken)
                        .ConfigureAwait(true);
                }
                finally
                {
                    value = string.Empty;
                }
            }

            _status.Show(ResultMessage(result));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _status.Show("Операция отменена. При следующем запуске выберите восстановление.");
            return null;
        }
        catch
        {
            // Never show exception text: downstream HTTP and process exceptions may contain secrets.
            _status.Show("Операция завершилась ошибкой. Запустите восстановление или обратитесь в поддержку.");
            return null;
        }
    }

    private static string BlockerMessage(string code) => code switch
    {
        "shared-common-access-runtime-factory-unavailable" =>
            "Безопасная конфигурация подключения пока не предоставлена общей службой Connector. Токен не запрашивался.",
        "ExactVpnPeerBindingUnavailable" => "Не удалось проверить привязку защищённого подключения. Токен не запрашивался.",
        "ExactDeviceSelfRevocationUnavailable" => "Сервер не подтвердил возможность безопасного отзыва старого устройства. Токен не запрашивался.",
        "CommonAccessCredentialContractIncomplete" => "Общий контракт доступа недоступен. Токен не запрашивался.",
        "PlatformAccessSchemaUnavailable" => "Сервис Platform недоступен или его схема не подтверждена. Токен не запрашивался.",
        "WindowsHostRequired" => "Запустите программу в Windows под исходной учётной записью Connector.",
        "MachineBoundaryNotReady" => "Не подтверждена защищённая системная среда. Токен не запрашивался.",
        "SignedReleasePinUnavailable" => "Не удалось подтвердить доверенный пакет обновления. Токен не запрашивался.",
        "ExistingOperationRequiresRecovery" => "Предыдущая операция уже записана. Выберите восстановление; новый токен не требуется.",
        "RecoveryJournalMissing" => "Незавершённая операция не найдена. Восстановление не требуется.",
        _ => "Проверка безопасности не пройдена. Токен не запрашивался."
    };

    private static string ResultMessage(UpgradeExecutionResult result) => result switch
    {
        { Outcome: UpgradeOutcome.Succeeded or UpgradeOutcome.AlreadyCommitted, RebootRequired: true } => "Перенос доступа завершён. Требуется перезагрузка Windows.",
        { Outcome: UpgradeOutcome.Succeeded } => "Перенос доступа успешно завершён.",
        { Outcome: UpgradeOutcome.AlreadyCommitted } => "Перенос доступа уже завершён ранее.",
        { Outcome: UpgradeOutcome.FailedAndRolledBack } => "Перенос остановлен; изменения откатаны.",
        { Outcome: UpgradeOutcome.NeedsManualRecovery } => "Требуется восстановление специалистом поддержки.",
        { Outcome: UpgradeOutcome.Interrupted } => "Найдена незавершённая операция. Выберите восстановление.",
        _ => "Операция завершена с неизвестным результатом."
    };
}
