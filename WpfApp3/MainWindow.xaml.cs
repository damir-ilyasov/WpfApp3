using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace WpfApp3;

public partial class MainWindow : Window
{
    private HttpListener? _listener;
    private bool _isRunning = false;
    private DateTime _startTime;
    private System.Windows.Threading.DispatcherTimer _uptimeTimer = new();

    private int _getCount = 0;
    private int _postCount = 0;
    private readonly List<long> _responseTimes = new();

    private readonly ObservableCollection<MessageItem> _messages = new();
    private int _messageIdCounter = 1;

    private readonly List<string> _allLogs = new();

    private readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
    });

    private readonly List<DateTime> _requestTimes = new();

    private int _peakMinute = 0;
    private int _peakHour = 0;

    private System.Windows.Threading.DispatcherTimer _loadTimer = new();

    public MainWindow()
    {
        InitializeComponent();
        MessagesListView.ItemsSource = _messages;

        _uptimeTimer.Interval = TimeSpan.FromSeconds(1);
        _uptimeTimer.Tick += (s, e) =>
        {
            if (_isRunning)
                TxtUptime.Text = (DateTime.Now - _startTime).ToString(@"hh\:mm\:ss");
        };

        _loadTimer.Interval = TimeSpan.FromSeconds(1);
        _loadTimer.Tick += (s, e) => UpdateLoadStats();
        _loadTimer.Start();
    }

    private void BtnStartStop_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning)
            StartServer();
        else
            StopServer();
    }

    private void StartServer()
    {
        if (!int.TryParse(TxtPort.Text, out var port))
        {
            MessageBox.Show("Некорректный порт.");
            return;
        }

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();

            _isRunning = true;
            _startTime = DateTime.Now;
            _uptimeTimer.Start();

            BtnStartStop.Content = "Остановить сервер";
            TxtServerStatus.Text = $"Работает на порту {port}";
            TxtServerStatus.Foreground = System.Windows.Media.Brushes.Green;
            TxtPort.IsEnabled = false;

            AppendLog($"[{Now()}] Сервер запущен на http://localhost:{port}/");

            Task.Run(ListenLoop);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка запуска: {ex.Message}");
        }
    }

    private void StopServer()
    {
        _isRunning = false;
        _listener?.Stop();
        _listener = null;
        _uptimeTimer.Stop();

        BtnStartStop.Content = "Запустить сервер";
        TxtServerStatus.Text = "Остановлен";
        TxtServerStatus.Foreground = System.Windows.Media.Brushes.Red;
        TxtPort.IsEnabled = true;
        TxtUptime.Text = "—";

        AppendLog($"[{Now()}] Сервер остановлен.");
    }

    private async Task ListenLoop()
    {
        while (_isRunning && _listener != null)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch
            {
                break;
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var req = context.Request;
        var resp = context.Response;

        var method = req.HttpMethod;
        var url = req.Url?.ToString() ?? "";
        var headers = string.Join(", ", req.Headers.AllKeys.Select(k => $"{k}={req.Headers[k]}"));

        string body = "";
        if (req.HasEntityBody)
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            body = await reader.ReadToEndAsync();
        }

        string responseBody;
        int statusCode = 200;

        if (method == "GET")
        {
            Dispatcher.Invoke(() => _getCount++);
            var info = new
            {
                status = "running",
                getRequests = _getCount,
                postRequests = _postCount,
                uptime = (DateTime.Now - _startTime).ToString(@"hh\:mm\:ss")
            };
            responseBody = JsonSerializer.Serialize(info);
        }
        else if (method == "POST")
        {
            Dispatcher.Invoke(() => _postCount++);
            try
            {
                var doc = JsonDocument.Parse(body);
                var message = doc.RootElement.GetProperty("message").GetString() ?? "";
                int newId = 0;

                Dispatcher.Invoke((Action)(() =>
                {
                    newId = _messageIdCounter++;
                    _messages.Add(new MessageItem
                    {
                        Id = newId,
                        Message = message,
                        Time = DateTime.Now.ToString("HH:mm:ss")
                    });
                }));

                responseBody = JsonSerializer.Serialize(new { id = newId, status = "saved" });
            }
            catch
            {
                statusCode = 400;
                responseBody = "{\"error\": \"Некорректный JSON\"}";
            }
        }
        else
        {
            statusCode = 405;
            responseBody = "{\"error\": \"Метод не поддерживается\"}";
        }

        sw.Stop();
        Dispatcher.Invoke(() =>
        {
            _requestTimes.Add(DateTime.Now);
        });
        var elapsed = sw.ElapsedMilliseconds;

        Dispatcher.Invoke(() =>
        {
            _responseTimes.Add(elapsed);
            TxtGetCount.Text = _getCount.ToString();
            TxtPostCount.Text = _postCount.ToString();
            TxtAvgTime.Text = $"{_responseTimes.Average():F1} мс";

            var logLine = $"[{Now()}] {method} {url} | Тело: {(string.IsNullOrEmpty(body) ? "—" : body)} | {elapsed}мс | {statusCode}";
            AppendLog(logLine, method);
        });

        var bytes = Encoding.UTF8.GetBytes(responseBody);
        resp.StatusCode = statusCode;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.OutputStream.Close();
    }

    private void AppendLog(string line, string? method = null)
    {
        var entry = method != null ? $"[{method}] {line}" : line;
        _allLogs.Add(entry);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (TxtServerLog == null || CmbServerFilter == null) return;

        var filter = (CmbServerFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Все";
        var filtered = filter == "Все"
            ? _allLogs
            : _allLogs.Where(l => l.StartsWith($"[{filter}]")).ToList();

        TxtServerLog.Text = string.Join("\n", filtered);
        TxtServerLog.ScrollToEnd();
    }

    private void CmbServerFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ClearServerLog_Click(object sender, RoutedEventArgs e)
    {
        _allLogs.Clear();
        TxtServerLog.Text = "";
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllLines("logs.txt", _allLogs);
            MessageBox.Show("Логи сохранены в logs.txt");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка: {ex.Message}");
        }
    }

    private void CmbMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtRequestBody == null) return;
        var method = (CmbMethod.SelectedItem as ComboBoxItem)?.Content?.ToString();
        TxtRequestBody.IsEnabled = method == "POST";
    }

    private async void SendRequest_Click(object sender, RoutedEventArgs e)
    {
        var url = TxtClientUrl.Text.Trim();
        var method = (CmbMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "GET";

        TxtClientResponse.Text = "Отправка запроса...";

        try
        {
            HttpResponseMessage response;

            if (method == "GET")
            {
                response = await _httpClient.GetAsync(url);
            }
            else
            {
                var body = TxtRequestBody.Text.Trim();
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                response = await _httpClient.PostAsync(url, content);
            }

            var responseBody = await response.Content.ReadAsStringAsync();

            try
            {
                var doc = JsonDocument.Parse(responseBody);
                responseBody = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            }
            catch { }

            var sb = new StringBuilder();
            sb.AppendLine($"Статус: {(int)response.StatusCode} {response.StatusCode}");
            sb.AppendLine($"Заголовки ответа:");
            foreach (var h in response.Headers)
                sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");
            sb.AppendLine();
            sb.AppendLine("Тело ответа:");
            sb.AppendLine(responseBody);

            TxtClientResponse.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            TxtClientResponse.Text = $"Ошибка: {ex.Message}";
        }
    }

    private void UpdateLoadStats()
    {
        var now = DateTime.Now;

        Dispatcher.Invoke(() =>
        {
            _requestTimes.RemoveAll(t => (now - t).TotalHours > 1);

            int lastMinute = _requestTimes.Count(t => (now - t).TotalSeconds <= 60);
            int lastHour = _requestTimes.Count;

            if (lastMinute > _peakMinute)
                _peakMinute = lastMinute;

            if (lastHour > _peakHour)
                _peakHour = lastHour;

            TxtLoadMinute.Text = lastMinute.ToString();
            TxtLoadHour.Text = lastHour.ToString();

            TxtPeakMinute.Text = _peakMinute.ToString();
            TxtPeakHour.Text = _peakHour.ToString();
        });
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isRunning) StopServer();
        _httpClient.Dispose();
    }

    private static string Now() => DateTime.Now.ToString("HH:mm:ss");
}