using System.Globalization;
using System.IO;

namespace DriveDownload;

internal sealed record Phrase(string Russian, string English)
{
    internal string Current => L.IsEnglish ? English : Russian;

    internal Phrase Format(params object[] arguments) => new(
        FormatFor(L.RussianCulture, Russian, arguments, english: false),
        FormatFor(L.EnglishCulture, English, arguments, english: true));

    internal string FormatCurrent(object[] arguments) => L.IsEnglish
        ? FormatFor(L.EnglishCulture, English, arguments, english: true)
        : FormatFor(L.RussianCulture, Russian, arguments, english: false);

    private static string FormatFor(CultureInfo culture, string template, object[] arguments, bool english) =>
        string.Format(culture, template, arguments.Select(argument =>
            argument is Phrase text ? (english ? text.English : text.Russian) : argument).ToArray());

    internal static Phrase From(Exception error) =>
        error is ILocalizedError localized ? localized.Text : new(error.Message, error.Message);
}

internal interface ILocalizedError
{
    Phrase Text { get; }
}

internal sealed class LocalizedArgumentException(Phrase text) : ArgumentException, ILocalizedError
{
    public Phrase Text { get; } = text;
    public override string Message => Text.Current;
}

internal sealed class LocalizedIOException(Phrase text) : IOException, ILocalizedError
{
    public Phrase Text { get; } = text;
    public override string Message => Text.Current;
}

internal static class L
{
    internal static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");
    internal static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    internal static volatile bool IsEnglish;
    internal static CultureInfo Culture => IsEnglish ? EnglishCulture : RussianCulture;

    internal static Phrase PhraseFor(string key) => Phrases[key];
    internal static string Tr(string key, params object[] arguments) =>
        arguments.Length == 0 ? PhraseFor(key).Current : PhraseFor(key).FormatCurrent(arguments);

    private static readonly Dictionary<string, Phrase> Phrases = new(StringComparer.Ordinal)
    {
        ["AppTitle"] = new("Диск Загрузки", "Drive Download"),
        ["AppSubtitle"] = new("Загрузка папок Google Drive", "Download Google Drive folders"),
        ["DriveSource"] = new("Источник файлов Google Drive", "Google Drive file source"),
        ["FolderLink"] = new("Ссылка на папку Google Drive", "Google Drive folder link"),
        ["SaveTo"] = new("Куда сохранить", "Save to"),
        ["Destination"] = new("Папка назначения", "Destination folder"),
        ["BrowseTip"] = new("Выбрать папку…", "Choose folder…"),
        ["Browse"] = new("Выбрать папку", "Choose folder"),
        ["ChooseFilesTip"] = new("Выбрать файлы…", "Choose files…"),
        ["ChooseFiles"] = new("Выбрать файлы", "Choose files"),
        ["DownloadAll"] = new("Скачать всё", "Download all"),
        ["DownloadResume"] = new("Скачать / продолжить", "Download / resume"),
        ["ResumeSelected"] = new("Продолжить выбранное", "Resume selected"),
        ["LocalFolder"] = new("Папка на компьютере", "Folder on this PC"),
        ["ProgressPercent"] = new("Процент загрузки", "Download percentage"),
        ["InitialSummary"] = new("0 Б загружено", "0 B downloaded"),
        ["InitialRate"] = new("Скорость появится при загрузке", "Speed appears during download"),
        ["OverallProgress"] = new("Общий прогресс загрузки", "Overall download progress"),
        ["Pause"] = new("Пауза", "Pause"),
        ["EnterLink"] = new("Вставьте ссылку на папку", "Enter a folder link"),
        ["OpenFolder"] = new("Открыть папку", "Open folder"),
        ["File"] = new("Файл", "File"),
        ["Downloaded"] = new("Загружено", "Downloaded"),
        ["FileProgress"] = new("Прогресс файла", "File progress"),
        ["Status"] = new("Состояние", "Status"),
        ["EmptyQueuePrompt"] = new("Файлы появятся после чтения папки", "Files appear after the folder is read"),
        ["EmptyQueue"] = new("Очередь пуста", "Queue is empty"),
        ["AccessSettings"] = new("Параметры доступа к Google Drive", "Google Drive access settings"),
        ["NotConnected"] = new("Google не подключён", "Google not connected"),
        ["Connected"] = new("Google подключён", "Google connected"),
        ["ApiAccess"] = new("Доступ по ключу API", "API key access"),
        ["Language"] = new("Язык интерфейса", "Interface language"),
        ["PickerTitle"] = new("Выбор файлов · Диск Загрузки", "Choose files · Drive Download"),
        ["FolderContents"] = new("Файлы в папке", "Files in the folder"),
        ["Refresh"] = new("Обновить", "Refresh"),
        ["SelectAll"] = new("Выбрать всё", "Select all"),
        ["ClearSelection"] = new("Снять выбор", "Clear selection"),
        ["ContentTree"] = new("Содержимое папки", "Folder contents"),
        ["ReadingFolder"] = new("Читаем папку Google Drive…", "Reading Google Drive folder…"),
        ["NoSelection"] = new("Нет выбранных файлов", "No files selected"),
        ["Close"] = new("Закрыть", "Close"),
        ["DownloadSelected"] = new("Скачать выбранное", "Download selected"),
        ["ReadingList"] = new("Читаем список…", "Reading file list…"),
        ["EmptyFolder"] = new("Папка пуста", "Folder is empty"),
        ["ReadFailed"] = new("Не удалось прочитать папку. Проверьте связь и нажмите «Обновить».", "Could not read the folder. Check your connection and click Refresh."),
        ["NothingSelected"] = new("Ничего не выбрано", "Nothing selected"),
        ["FileCount"] = new("Файлов: {0}", "Files: {0}"),
        ["EmptyFolderCount"] = new("Пустых папок: {0}", "Empty folders: {0}"),
        ["KnownSize"] = new("Известный объём: {0}", "Known size: {0}"),
        ["Unavailable"] = new("Недоступно", "Unavailable"),
        ["AlreadyOpen"] = new("Загрузчик уже открыт.", "Drive Download is already open."),
        ["SavedSettingsFailed"] = new("Сохранённые настройки не открылись. Проверьте подключение и папку.", "Could not open saved settings. Check your connection and folder."),
        ["ConnectToResume"] = new("Подключите Google, чтобы продолжить сохранённую загрузку.", "Connect Google to resume your saved download."),
        ["SavingKey"] = new("Сохраняем ключ API…", "Saving API key…"),
        ["FinishLogin"] = new("Завершите вход в Google в браузере.", "Finish signing in to Google in your browser."),
        ["ConnectionSaved"] = new("Подключение сохранено.", "Connection saved."),
        ["ConnectionCanceled"] = new("Подключение отменено или время входа истекло.", "Sign-in was canceled or timed out."),
        ["ConnectionFailed"] = new("Не удалось прочитать настройки или связаться с Google.", "Could not read the settings or connect to Google."),
        ["DownloadFolder"] = new("Папка для загрузок", "Download folder"),
        ["FullPathRequired"] = new("Выберите папку назначения по полному пути.", "Choose a destination folder using its full path."),
        ["SaveJobFailed"] = new("Windows не смог сохранить задание.", "Windows could not save the download job."),
        ["Paused"] = new("Пауза. Полученные данные сохранены.", "Paused. Downloaded data has been saved."),
        ["DownloadFailed"] = new("Ошибка загрузчика. Сохранённые данные можно использовать при повторном запуске.", "Download failed. You can retry using the saved data."),
        ["CancelLogin"] = new("Отменить вход", "Cancel sign-in"),
        ["OpenFolderFailed"] = new("Windows не смог открыть папку.", "Windows could not open the folder."),
        ["DownloadAmount"] = new("{0} из {1}", "{0} of {1}"),
        ["Speed"] = new("{0}/с", "{0}/s"),
        ["SpeedEta"] = new("{0}  ·  осталось {1}", "{0}  ·  {1} left"),
        ["TransferComplete"] = new("Передача завершена", "Transfer complete"),
        ["QueueCount"] = new("{0} из {1} файлов", "{0} of {1} files"),
        ["ErrorCount"] = new("Ошибок: {0}", "Errors: {0}"),
        ["HoursMinutes"] = new("{0} ч {1:D2} мин", "{0}h {1:D2}m"),
        ["MinutesSeconds"] = new("{0} мин {1:D2} с", "{0}m {1:D2}s"),
        ["Seconds"] = new("{0} с", "{0}s"),
        ["LanguageSaveFailed"] = new("Язык изменён. Сохранить выбор не удалось.", "Language changed. Could not save your preference."),
        ["ConnectionTitle"] = new("Подключение к Google Drive", "Connect to Google Drive"),
        ["SignInTitle"] = new("Вход в Google Drive", "Sign in to Google Drive"),
        ["OAuthInstructions"] = new("Для папок вашего аккаунта выберите JSON клиента OAuth Desktop app. Вход откроется в браузере. Приложение запросит доступ к Drive только для чтения.", "For folders in your account, choose a Desktop app OAuth client JSON file. Sign-in opens in your browser. Drive Download requests read-only access."),
        ["ChooseJson"] = new("Выбрать JSON и войти", "Choose JSON and sign in"),
        ["OAuthPickerTitle"] = new("OAuth-клиент Google для Desktop app", "Google Desktop app OAuth client"),
        ["ApiInstructions"] = new("Для папок с публичным доступом можно использовать ключ Google Drive API.", "For public folders, you can use a Google Drive API key."),
        ["ApiKey"] = new("Ключ Google Drive API", "Google Drive API key"),
        ["UseApiKey"] = new("Использовать ключ API", "Use API key"),
        ["MustBeFolder"] = new("Ссылка должна вести на папку Google Drive.", "The link must point to a Google Drive folder."),
        ["ShortcutLoop"] = new("Ярлык образует цикл папок.", "This shortcut creates a folder loop."),
        ["ShortcutUnavailable"] = new("Цель ярлыка недоступна.", "The shortcut target is unavailable."),
        ["OwnerBlocked"] = new("Владелец запретил скачивание.", "The owner has disabled downloads."),
        ["ExportUnsupported"] = new("Этот тип Google Workspace нельзя скачать как обычный файл.", "This Google Workspace type cannot be downloaded as a regular file."),
        ["Queued"] = new("В очереди", "Queued"),
        ["CacheFailed"] = new("Не удалось открыть кэш задания.", "Could not open the download cache."),
        ["SelectSomething"] = new("Отметьте файлы или папки для скачивания.", "Select files or folders to download."),
        ["SelectionMissing"] = new("Отмеченных объектов больше нет в папке Google Drive.", "The selected items are no longer in the Google Drive folder."),
        ["Error"] = new("Ошибка", "Error"),
        ["CheckingLocal"] = new("Сверяем локальные файлы…", "Checking local files…"),
        ["CompletedMissing"] = new("Завершено. Отмеченных объектов, которых больше нет в папке: {0}.", "Finished. Selected items no longer in the folder: {0}."),
        ["CompletedWithErrors"] = new("Завершено с ошибками. Подробности в списке.", "Finished with errors. See the file list for details."),
        ["DownloadComplete"] = new("Загрузка завершена.", "Download complete."),
        ["RetryAt"] = new("{0} Повтор в {1:HH:mm:ss}.", "{0} Retrying at {1:HH:mm:ss}."),
        ["ReceivingFiles"] = new("Получаем файлы с Google Drive", "Getting files from Google Drive"),
        ["AlreadyLocal"] = new("Уже на диске", "Already on disk"),
        ["LocalConflict"] = new("В папке есть файл с этим именем и другим содержимым. Выберите другую папку или переместите локальный файл.", "A file with this name already exists and has different content. Choose another folder or move the local file."),
        ["ResumingAt"] = new("Продолжаем с {0}", "Resuming at {0}"),
        ["Downloading"] = new("Скачиваем", "Downloading"),
        ["UnknownEncoding"] = new("Google вернул неожиданное кодирование файла.", "Google returned an unexpected file encoding."),
        ["PageInsteadOfFile"] = new("Drive вернул веб-страницу вместо файла.", "Drive returned a web page instead of the file."),
        ["BadRange"] = new("Google вернул неверный диапазон байтов.", "Google returned an invalid byte range."),
        ["WrongResponseSize"] = new("Размер ответа Drive отличается от размера файла.", "The Drive response size differs from the file size."),
        ["OffsetRejected"] = new("Drive больше не принимает сохранённое смещение. Обновите список папки.", "Drive no longer accepts the saved offset. Refresh the folder list."),
        ["PartialTransfer"] = new("Drive передал часть файла. Продолжаем с сохранённого места.", "Drive sent only part of the file. Resuming from the saved offset."),
        ["CheckingFile"] = new("Проверяем файл", "Verifying file"),
        ["FileCheckFailed"] = new("Проверка файла не прошла. Drive мог изменить исходный файл.", "File verification failed. The original file may have changed on Drive."),
        ["Ready"] = new("Готово", "Done"),
        ["DriveInterrupted"] = new("Связь с Drive прервалась.", "The connection to Drive was interrupted."),
        ["TooManyBytes"] = new("Drive прислал больше данных, чем ожидалось.", "Drive sent more data than expected."),
        ["ReparsePoint"] = new("Путь содержит ссылку или точку подключения: {0}", "The path contains a link or mount point: {0}"),
        ["ConnectionInterrupted"] = new("Связь с Google Drive прервана.", "The connection to Google Drive was interrupted."),
        ["DesktopJsonRequired"] = new("Выберите JSON OAuth-клиента типа Desktop app.", "Choose an OAuth client JSON file of the Desktop app type."),
        ["InvalidClientId"] = new("В JSON отсутствует действительный client_id Google.", "The JSON file does not contain a valid Google client_id."),
        ["ReturnToApp"] = new("Вернитесь в приложение «Диск Загрузки».", "Return to the Drive Download app."),
        ["LoginIncomplete"] = new("Вход Google не завершён. Повторите подключение.", "Google sign-in did not finish. Try connecting again."),
        ["NoRepeatPermission"] = new("Google не выдал разрешение для повторного входа. Подключите аккаунт ещё раз.", "Google did not grant permission to renew access. Connect your account again."),
        ["RefreshUnavailable"] = new("Google временно не обновляет подключение.", "Google cannot refresh access right now."),
        ["RefreshRejected"] = new("Google отклонил подключение. Проверьте OAuth-клиент и войдите снова.", "Google rejected the connection. Check the OAuth client and sign in again."),
        ["MissingAccessToken"] = new("Google не вернул токен доступа.", "Google did not return an access token."),
        ["MissingFileInfo"] = new("Drive вернул пустые сведения о файле.", "Drive returned no file details."),
        ["MissingFileList"] = new("Drive вернул пустой список файлов.", "Drive returned no file list."),
        ["IncompleteSearch"] = new("Google вернул неполный список папки. Повторяем запрос.", "Google returned an incomplete folder listing. Retrying the request."),
        ["Quota"] = new("Google Drive ограничил скачивание. Ожидаем следующей попытки.", "Google Drive limited downloads. Waiting to retry."),
        ["DriveUnavailable"] = new("Google Drive временно недоступен (HTTP {0}).", "Google Drive is temporarily unavailable (HTTP {0})."),
        ["Reconnect"] = new("Подключите аккаунт Google заново.", "Reconnect your Google account."),
        ["EnableDriveApi"] = new("В проекте Google Cloud нужно включить Google Drive API.", "Enable the Google Drive API in your Google Cloud project."),
        ["ItemUnavailable"] = new("Папка или файл недоступны. Проверьте ссылку и аккаунт Google.", "The folder or file is unavailable. Check the link and your Google account."),
        ["DownloadForbidden"] = new("Google Drive запретил скачивание этого объекта. Проверьте доступ.", "Google Drive blocked this download. Check your access."),
        ["RequestRejected"] = new("Google Drive отклонил запрос (HTTP {0}).", "Google Drive rejected the request (HTTP {0})."),
        ["ProtectedDataFailed"] = new("Windows не смог открыть защищённые данные подключения.", "Windows could not open the protected connection settings."),
        ["HttpsLinkRequired"] = new("Вставьте HTTPS-ссылку на папку Google Drive.", "Enter an HTTPS link to a Google Drive folder."),
        ["InvalidFolderId"] = new("В ссылке не найден идентификатор папки Google Drive.", "The link does not contain a valid Google Drive folder ID."),
    };
}
