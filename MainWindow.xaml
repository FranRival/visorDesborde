using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VisorDesborde;

public partial class MainWindow : Window
{
    private const int LimitePorCarpeta = 60;
    private static readonly HashSet<string> ExtensionesImagen = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"
    };

    // Log junto al .exe: si algo falla, queda rastro aunque el proceso muera.
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "visorDesborde.log");
    private static readonly object LogLock = new();

    private string? _carpetaMadre;
    private readonly DispatcherTimer _ramTimer;
    private readonly ObservableCollection<CarpetaOverflowInfo> _carpetasConDesborde = [];
    private readonly ObservableCollection<ImagenPreviewItem> _imagenesActuales = [];
    private List<PlanDesborde> _ultimoPlan = [];

    private CancellationTokenSource? _previewCts;
    private bool _ocupado;

    public MainWindow()
    {
        InitializeComponent();

        // Red de seguridad global. Las excepciones que escapan de un async void se re-lanzan
        // en el SynchronizationContext de WPF y, sin handler, terminan el proceso.
        if (Application.Current is not null)
            Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("UnobservedTask: " + e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("AppDomain (fatal): " + e.ExceptionObject);

        LstCarpetasDesborde.ItemsSource = _carpetasConDesborde;
        ItemsImagenes.ItemsSource = _imagenesActuales;

        // Timer cada 500 ms para medir RAM sin saturar el hilo de UI
        _ramTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _ramTimer.Tick += (_, _) => ActualizarRam();
        _ramTimer.Start();
        ActualizarRam();
    }

    // ───────────────────────── Infraestructura ─────────────────────────

    private static void Log(string mensaje)
    {
        try
        {
            lock (LogLock)
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {mensaje}{Environment.NewLine}");
        }
        catch { /* el log nunca debe tumbar la app */ }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Dispatcher: " + e.Exception);
        TxtEstado.Text = $"Error controlado: {e.Exception.Message} (ver visorDesborde.log)";
        SetBotonesHabilitados(true);
        e.Handled = true; // evita que el runtime cierre el proceso
    }

    private void ActualizarRam()
    {
        using var proc = Process.GetCurrentProcess();
        double workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
        double gcMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

        TxtRamProceso.Text = $"{workingSetMb:F1} MB";
        TxtRamGc.Text = $"{gcMb:F1} MB";
    }

    // ───────────────────────── Selección / Escaneo ─────────────────────────

    private void BtnSeleccionar_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Selecciona la carpeta madre"
        };

        if (dialog.ShowDialog() == true)
        {
            _carpetaMadre = dialog.FolderName;
            TxtEstado.Text = $"Carpeta madre: {_carpetaMadre}";
            BtnEscanear.IsEnabled = true;
            BtnSimular.IsEnabled = false;
            BtnAplicar.IsEnabled = false;
            _ultimoPlan = [];
            LimpiarVisor();
            _carpetasConDesborde.Clear();
        }
    }

    private async void BtnEscanear_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_carpetaMadre) || !Directory.Exists(_carpetaMadre))
            return;

        SetBotonesHabilitados(false);
        LimpiarVisor();
        _carpetasConDesborde.Clear();
        _ultimoPlan = [];
        TxtEstado.Text = "Escaneando carpetas e imágenes...";

        string raiz = _carpetaMadre;
        var progreso = new Progress<string>(m => TxtEstado.Text = m);

        try
        {
            var resultados = await Task.Run(() => EscanearCarpetas(raiz, progreso));

            foreach (var item in resultados)
                _carpetasConDesborde.Add(item);

            TxtEstado.Text = $"Escaneo completo. Carpetas con >{LimitePorCarpeta} imágenes: {_carpetasConDesborde.Count}";
        }
        catch (Exception ex)
        {
            Log("Escaneo: " + ex);
            TxtEstado.Text = $"Error en el escaneo: {ex.Message} (ver visorDesborde.log)";
        }
        finally
        {
            SetBotonesHabilitados(true);
        }
    }

    private static List<CarpetaOverflowInfo> EscanearCarpetas(string raiz, IProgress<string> progreso)
    {
        var encontrados = new List<CarpetaOverflowInfo>();

        var opcionesDir = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var opcionesFile = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true
        };

        int revisadas = 0;

        try
        {
            foreach (string dir in Directory.EnumerateDirectories(raiz, "*", opcionesDir))
            {
                if (++revisadas % 50 == 0)
                    progreso.Report($"Escaneando... {revisadas} carpetas revisadas, {encontrados.Count} con desborde");

                try
                {
                    var imagenes = Directory.EnumerateFiles(dir, "*.*", opcionesFile)
                        .Where(f => ExtensionesImagen.Contains(Path.GetExtension(f)))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (imagenes.Count > LimitePorCarpeta)
                    {
                        encontrados.Add(new CarpetaOverflowInfo
                        {
                            FullPath = dir,
                            RelativePath = Path.GetRelativePath(raiz, dir),
                            TotalImagenes = imagenes.Count,
                            SubcarpetasNecesarias = (int)Math.Ceiling(imagenes.Count / (double)LimitePorCarpeta),
                            Archivos = imagenes
                        });
                    }
                }
                catch (Exception ex)
                {
                    // Una carpeta problemática (ruta larga, permisos, etc.) no aborta el escaneo completo.
                    Log($"Escaneo: se omitió '{dir}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log("Escaneo (enumeración): " + ex);
        }

        return encontrados.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ───────────────────────── Quitar de la lista (X) ─────────────────────────

    private void BtnQuitar_Click(object sender, RoutedEventArgs e)
    {
        if (_ocupado) return;
        if (sender is not Button { Tag: CarpetaOverflowInfo item }) return;

        // Si era la seleccionada, Remove() dispara SelectionChanged con SelectedItem == null
        // y el handler limpia el visor y cancela la carga de miniaturas en curso.
        _carpetasConDesborde.Remove(item);

        // El plan simulado ya no es válido (incluye la carpeta quitada): se descarta.
        if (_ultimoPlan.Count > 0)
        {
            _ultimoPlan = [];
            BtnAplicar.IsEnabled = false;
        }
        BtnSimular.IsEnabled = _carpetasConDesborde.Count > 0;

        TxtEstado.Text = $"Quitada de la lista: {item.RelativePath}. Quedan {_carpetasConDesborde.Count}. (No se tocó el disco)";
    }

    // ───────────────────────── Simulación ─────────────────────────

    private async void BtnSimular_Click(object sender, RoutedEventArgs e)
    {
        if (_carpetasConDesborde.Count == 0) return;

        SetBotonesHabilitados(false);
        TxtEstado.Text = "Calculando plan de desborde (simulación)...";

        // Snapshot en el hilo de UI: enumerar la ObservableCollection desde el pool no es thread-safe.
        var snapshot = _carpetasConDesborde.ToList();

        try
        {
            _ultimoPlan = await Task.Run(() => ConstruirPlanDesborde(snapshot));

            int carpetasNuevas = _ultimoPlan.Sum(p => p.Lotes.Count(l => !l.EsOriginal));
            int archivosAMover = _ultimoPlan.Sum(p => p.Lotes.Where(l => !l.EsOriginal).Sum(l => l.Archivos.Count));

            TxtEstado.Text = $"Simulación: se crearían {carpetasNuevas} carpetas hermanas para {archivosAMover} imágenes " +
                             $"(cada original conserva las primeras {LimitePorCarpeta}).";
        }
        catch (Exception ex)
        {
            Log("Simulación: " + ex);
            _ultimoPlan = [];
            TxtEstado.Text = $"Error al simular: {ex.Message} (ver visorDesborde.log)";
        }
        finally
        {
            SetBotonesHabilitados(true);
        }
    }

    private static List<PlanDesborde> ConstruirPlanDesborde(IEnumerable<CarpetaOverflowInfo> carpetas)
    {
        var planes = new List<PlanDesborde>();

        // Destinos ya asignados en este plan (global) para no pisarse entre carpetas.
        var reservados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var carpeta in carpetas)
        {
            string rutaLimpia = Path.TrimEndingDirectorySeparator(carpeta.FullPath);
            string nombre = Path.GetFileName(rutaLimpia);
            string? padre = Path.GetDirectoryName(rutaLimpia);
            if (string.IsNullOrEmpty(padre)) continue; // raíz de unidad: no hay "nivel hermano"

            var lotes = new List<LoteSubcarpeta>();
            int siguienteSufijo = 2;

            for (int i = 0, lote = 0; i < carpeta.Archivos.Count; i += LimitePorCarpeta, lote++)
            {
                var chunk = carpeta.Archivos.Skip(i).Take(LimitePorCarpeta).ToList();

                if (lote == 0)
                {
                    // Lote 1: se queda en la carpeta original (no hay I/O).
                    lotes.Add(new LoteSubcarpeta
                    {
                        Indice = 1,
                        DestinoDir = carpeta.FullPath,
                        EsOriginal = true,
                        Archivos = chunk
                    });
                    continue;
                }

                // Lotes 2..N: carpeta hermana "<Nombre> <n>". Si el nombre ya existe en disco
                // (p. ej. "Etocha_-_Lilie 2" ya es una carpeta real) o fue reservado, se salta al siguiente sufijo libre.
                string destino;
                int sufijo;
                do
                {
                    sufijo = siguienteSufijo++;
                    destino = Path.Combine(padre, $"{nombre} {sufijo}");
                }
                while (reservados.Contains(destino) || Directory.Exists(destino));

                reservados.Add(destino);
                lotes.Add(new LoteSubcarpeta
                {
                    Indice = sufijo,
                    DestinoDir = destino,
                    EsOriginal = false,
                    Archivos = chunk
                });
            }

            planes.Add(new PlanDesborde
            {
                CarpetaOrigen = carpeta.FullPath,
                Lotes = lotes
            });
        }

        return planes;
    }

    // ───────────────────────── Aplicación ─────────────────────────

    private async void BtnAplicar_Click(object sender, RoutedEventArgs e)
    {
        if (_ultimoPlan.Count == 0) return;

        bool copiar = ChkCopiarEnVezDeMover.IsChecked == true;
        string accion = copiar ? "copiar" : "mover";

        int carpetasNuevas = _ultimoPlan.Sum(p => p.Lotes.Count(l => !l.EsOriginal));
        int archivos = _ultimoPlan.Sum(p => p.Lotes.Where(l => !l.EsOriginal).Sum(l => l.Archivos.Count));

        var confirm = MessageBox.Show(
            $"Se van a {accion} {archivos} imágenes a {carpetasNuevas} carpetas nuevas, creadas al mismo nivel que la carpeta original " +
            $"con el mismo nombre + sufijo (2, 3, 4...).\n\nLa carpeta original conserva las primeras {LimitePorCarpeta}.\n\n¿Continuar?",
            "Confirmar desborde",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        SetBotonesHabilitados(false);
        LimpiarVisor();
        TxtEstado.Text = $"Ejecutando desborde ({accion})...";

        var planActual = _ultimoPlan;
        var progreso = new Progress<string>(m => TxtEstado.Text = m);

        try
        {
            var res = await Task.Run(() => EjecutarPlan(planActual, copiar, progreso));

            _ultimoPlan = [];
            BtnAplicar.IsEnabled = false;
            TxtEstado.Text = $"Desborde finalizado: {res.Ok} OK, {res.Errores} con error. Vuelve a escanear para verificar.";

            if (res.Errores > 0)
            {
                MessageBox.Show(
                    $"Terminó con {res.Errores} archivos con error (de {res.Ok + res.Errores}).\nDetalle en:\n{LogPath}",
                    "Desborde con errores", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Log("Aplicar: " + ex);
            TxtEstado.Text = $"Error al aplicar: {ex.Message} (ver visorDesborde.log)";
        }
        finally
        {
            SetBotonesHabilitados(true);
        }
    }

    private static ResultadoAplicacion EjecutarPlan(List<PlanDesborde> plan, bool copiar, IProgress<string> progreso)
    {
        int total = plan.Sum(p => p.Lotes.Where(l => !l.EsOriginal).Sum(l => l.Archivos.Count));
        int ok = 0, errores = 0, procesados = 0;

        foreach (var carpeta in plan)
        {
            foreach (var lote in carpeta.Lotes)
            {
                if (lote.EsOriginal) continue; // el lote 1 ya está en su sitio

                try
                {
                    Directory.CreateDirectory(lote.DestinoDir);
                }
                catch (Exception ex)
                {
                    Log($"No se pudo crear '{lote.DestinoDir}': {ex.Message}");
                    errores += lote.Archivos.Count;
                    procesados += lote.Archivos.Count;
                    continue;
                }

                foreach (string origen in lote.Archivos)
                {
                    procesados++;
                    try
                    {
                        string destino = Path.Combine(lote.DestinoDir, Path.GetFileName(origen));

                        if (File.Exists(destino))
                        {
                            Log($"Omitido (ya existe): {destino}");
                            errores++;
                        }
                        else
                        {
                            if (copiar) File.Copy(origen, destino, overwrite: false);
                            else File.Move(origen, destino);
                            ok++;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Por archivo: IOException (en uso), UnauthorizedAccess, PathTooLong, etc.
                        Log($"Error con '{origen}': {ex.Message}");
                        errores++;
                    }

                    if (procesados % 20 == 0 || procesados == total)
                        progreso.Report($"Procesando {procesados}/{total}  (errores: {errores})");
                }
            }
        }

        return new ResultadoAplicacion(ok, errores);
    }

    // ───────────────────────── Vista previa ─────────────────────────

    private async void LstCarpetasDesborde_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Cancela la carga anterior: evita que dos cargas concurrentes mezclen miniaturas en la colección.
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;

        if (LstCarpetasDesborde.SelectedItem is not CarpetaOverflowInfo seleccionada)
        {
            LimpiarVisor();
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        var token = cts.Token;

        LimpiarVisor();
        TxtEstado.Text = $"Cargando vista previa (primeras {LimitePorCarpeta} de {seleccionada.TotalImagenes})...";

        var muestra = seleccionada.Archivos.Take(LimitePorCarpeta).ToList();

        try
        {
            var items = await Task.Run(() => CargarMiniaturas(muestra, token), token);

            if (token.IsCancellationRequested) return;

            foreach (var item in items)
                _imagenesActuales.Add(item);

            TxtEstado.Text = $"{seleccionada.RelativePath} — {seleccionada.TotalImagenes} imágenes " +
                             $"(+{seleccionada.Exceso} sobre el límite, {seleccionada.SubcarpetasNecesarias} carpetas)";
        }
        catch (OperationCanceledException)
        {
            // Selección cambió antes de terminar: normal.
        }
        catch (Exception ex)
        {
            Log("Vista previa: " + ex);
            TxtEstado.Text = $"Error en vista previa: {ex.Message} (ver visorDesborde.log)";
        }
    }

    private static List<ImagenPreviewItem> CargarMiniaturas(List<string> rutas, CancellationToken token)
    {
        var lista = new List<ImagenPreviewItem>(rutas.Count);

        foreach (string ruta in rutas)
        {
            token.ThrowIfCancellationRequested();

            BitmapImage? bmp = null;
            try
            {
                bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(ruta);
                // OnLoad: decodifica en EndInit y suelta el handle del archivo.
                // Con OnDemand el stream queda abierto y bloquea el File.Move posterior.
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = 160;
                bmp.EndInit();
                bmp.Freeze();
            }
            catch (Exception ex)
            {
                // WPF no decodifica .webp nativamente (sin codec WIC), y un archivo corrupto
                // lanza NotSupportedException / FileFormatException. Se muestra sin miniatura.
                Log($"Miniatura no disponible '{ruta}': {ex.Message}");
                bmp = null;
            }

            lista.Add(new ImagenPreviewItem
            {
                FileName = Path.GetFileName(ruta),
                Thumbnail = bmp
            });
        }

        return lista;
    }

    private void LimpiarVisor()
    {
        _imagenesActuales.Clear();
    }

    private void SetBotonesHabilitados(bool habilitado)
    {
        _ocupado = !habilitado;
        BtnSeleccionar.IsEnabled = habilitado;
        BtnEscanear.IsEnabled = habilitado && !string.IsNullOrWhiteSpace(_carpetaMadre);
        BtnSimular.IsEnabled = habilitado && _carpetasConDesborde.Count > 0;
        BtnAplicar.IsEnabled = habilitado && _ultimoPlan.Count > 0;
    }

    // ───────────────────────── Modelos ─────────────────────────

    public sealed class CarpetaOverflowInfo
    {
        public string FullPath { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public int TotalImagenes { get; init; }
        public int SubcarpetasNecesarias { get; init; }
        public List<string> Archivos { get; init; } = [];
        public int Exceso => TotalImagenes - LimitePorCarpeta;
        public string Resumen => $"{TotalImagenes} imgs (+{Exceso}) → {SubcarpetasNecesarias} carpetas (1..{SubcarpetasNecesarias})";
    }

    public sealed class ImagenPreviewItem
    {
        public string FileName { get; init; } = string.Empty;
        public BitmapImage? Thumbnail { get; init; }
    }

    private sealed class PlanDesborde
    {
        public string CarpetaOrigen { get; init; } = string.Empty;
        public List<LoteSubcarpeta> Lotes { get; init; } = [];
    }

    private sealed class LoteSubcarpeta
    {
        public int Indice { get; init; }
        public string DestinoDir { get; init; } = string.Empty;
        public bool EsOriginal { get; init; }
        public List<string> Archivos { get; init; } = [];
    }

    private readonly record struct ResultadoAplicacion(int Ok, int Errores);
}
