using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenRecorderLib;

namespace VideoEditor;

public partial class MainWindow : Window
{
    record SourceItem(string Name, RecordingSourceBase Source)
    {
        public override string ToString() => Name;
    }

    Recorder? _rec;
    string? _file;
    TimeSpan? _in, _out;
    bool _playing, _syncingSeek;
    readonly ObservableCollection<Segment> _segments = [];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    static readonly string RecordingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoEditor");

    public MainWindow()
    {
        InitializeComponent();
        SegmentList.ItemsSource = _segments;
        _timer.Tick += (_, _) => SyncTime();
        RefreshSources();
    }

    void SetStatus(string text) => StatusText.Text = text;

    // ---------- Recording ----------

    void RefreshSources()
    {
        var screens = Recorder.GetDisplays()
            .Select(d => new SourceItem("Screen: " + d.FriendlyName, new DisplayRecordingSource(d.DeviceName)));
        var windows = Recorder.GetWindows()
            .Where(w => w.IsValidWindow() && !w.IsMinmimized() && !string.IsNullOrWhiteSpace(w.Title))
            .Select(w => new SourceItem("Window: " + w.Title, new WindowRecordingSource(w.Handle)));
        SourceBox.ItemsSource = screens.Concat(windows).ToList();
        SourceBox.SelectedIndex = 0;
    }

    void RefreshSources_Click(object sender, RoutedEventArgs e) => RefreshSources();

    void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_rec != null)
        {
            RecordButton.IsEnabled = false;
            SetStatus("Finishing recording…");
            _rec.Stop();
            return;
        }
        if (SourceBox.SelectedItem is not SourceItem src) return;

        Directory.CreateDirectory(RecordingsDir);
        var path = Path.Combine(RecordingsDir, $"rec_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        var audioSources = new List<AudioSourceBase>();
        if (MicBox.IsChecked == true) audioSources.Add(CaptureAudioSource.Default);           // default mic
        if (SystemAudioBox.IsChecked == true) audioSources.Add(LoopbackAudioSource.Default);  // default speakers

        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = [src.Source] },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = audioSources.Count > 0,
                AudioSources = audioSources,
                Bitrate = AudioBitrate.bitrate_192kbps,
                Channels = AudioChannels.Stereo,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Framerate = FpsBox.SelectedIndex == 0 ? 30 : 60,
                Bitrate = 16_000 * 1000,
                IsFixedFramerate = true,
                IsHardwareEncodingEnabled = true, // GPU encoder (NVENC / AMF / Quick Sync)
                IsFragmentedMp4Enabled = true,    // file stays playable if the app or PC crashes mid-recording
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.CBR,
                    EncoderProfile = H264Profile.High,
                },
            },
        };

        // Recorder events fire on a background thread. BeginInvoke (not Invoke) so disposing the
        // recorder from the UI thread can't deadlock against the thread that raised the event.
        _rec = Recorder.CreateRecorder(options);
        _rec.OnRecordingComplete += (_, ev) => Dispatcher.BeginInvoke(() =>
        {
            EndRecording();
            LoadVideo(ev.FilePath);
            SetStatus("Saved " + ev.FilePath);
        });
        _rec.OnRecordingFailed += (_, ev) => Dispatcher.BeginInvoke(() =>
        {
            EndRecording();
            SetStatus($"Recording failed: {ev.Error}. Any partial file is in {RecordingsDir}");
        });
        _rec.OnStatusChanged += (_, ev) => Dispatcher.BeginInvoke(() =>
        {
            if (ev.Status == RecorderStatus.Recording) SetStatus("Recording to " + path);
        });

        ClosePlayer();
        _rec.Record(path);
        RecordButton.Content = "■ Stop";
        SourceBox.IsEnabled = false;
    }

    void EndRecording()
    {
        _rec?.Dispose();
        _rec = null;
        RecordButton.Content = "● _Record";
        RecordButton.IsEnabled = true;
        SourceBox.IsEnabled = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_rec != null &&
            MessageBox.Show(this, "A recording is in progress. Stop it and quit?", "Video Editor",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _rec?.Stop();
        base.OnClosing(e);
    }

    // ---------- Editing ----------

    void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "MP4 video|*.mp4|All files|*.*", InitialDirectory = RecordingsDir };
        if (dlg.ShowDialog(this) == true) LoadVideo(dlg.FileName);
    }

    void LoadVideo(string path)
    {
        _file = path;
        _segments.Clear();
        _in = _out = null;
        UpdateMarks();
        Player.Source = new Uri(path);
        Player.Play(); // Manual mode needs Play to open the media; paused again in MediaOpened
        Title = "Video Editor – " + Path.GetFileName(path);
    }

    void ClosePlayer()
    {
        _timer.Stop();
        Player.Close();
        Player.Source = null;
        _playing = false;
        PlayButton.Content = "▶ _Play";
    }

    void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        _playing = false;
        PlayButton.Content = "▶ _Play";
        Seek.Maximum = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 1;
        _timer.Start();
        SyncTime();
    }

    void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        _playing = false;
        PlayButton.Content = "▶ _Play";
    }

    void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e) =>
        SetStatus("Can't play this file: " + e.ErrorException.Message);

    void SyncTime()
    {
        _syncingSeek = true;
        Seek.Value = Player.Position.TotalSeconds;
        _syncingSeek = false;
        TimeText.Text = $@"{Player.Position:hh\:mm\:ss\.f} / {TimeSpan.FromSeconds(Seek.Maximum):hh\:mm\:ss\.f}";
    }

    void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncingSeek && _file != null) Player.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_file == null) return;
        if (_playing) Player.Pause(); else Player.Play();
        _playing = !_playing;
        PlayButton.Content = _playing ? "❚❚ _Pause" : "▶ _Play";
    }

    void SetIn_Click(object sender, RoutedEventArgs e) { if (_file != null) { _in = Player.Position; UpdateMarks(); } }
    void SetOut_Click(object sender, RoutedEventArgs e) { if (_file != null) { _out = Player.Position; UpdateMarks(); } }

    void UpdateMarks() =>
        MarkText.Text = $@"In: {(_in is { } i ? i.ToString(@"hh\:mm\:ss\.f") : "–")}   Out: {(_out is { } o ? o.ToString(@"hh\:mm\:ss\.f") : "–")}";

    void AddSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_in is not { } start || _out is not { } end || end <= start)
        {
            SetStatus("Set an In point before an Out point first.");
            return;
        }
        var merged = Cutter.Normalize(_segments.Append(new Segment(start, end)));
        _segments.Clear();
        foreach (var s in merged) _segments.Add(s);
        _in = _out = null;
        UpdateMarks();
    }

    void RemoveSegment_Click(object sender, RoutedEventArgs e)
    {
        if (SegmentList.SelectedItem is Segment s) _segments.Remove(s);
    }

    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_file == null) { SetStatus("Record or open a video first."); return; }

        var dlg = new SaveFileDialog
        {
            Filter = "MP4 video|*.mp4",
            InitialDirectory = Path.GetDirectoryName(_file),
            FileName = Path.GetFileNameWithoutExtension(_file) + "_edit.mp4",
        };
        if (dlg.ShowDialog(this) != true) return;

        ExportButton.IsEnabled = false;
        SetStatus("Exporting…");
        try
        {
            await Cutter.ExportAsync(_file, _segments.ToList(), dlg.FileName);
            SetStatus("Exported " + dlg.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("Export failed: " + ex.Message);
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }
}
