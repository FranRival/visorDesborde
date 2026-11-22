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

    private string? _carpetaMadre;
    private readonly DispatcherTimer _ramTimer;
    private readonly ObservableCollection<CarpetaOverflowInfo> _carpetasConDesborde = [];
    private readonly ObservableCollection<ImagenPreviewItem> _imagenesActuales = [];
    private List<PlanDesborde> _ultimoPlan = [];

    public MainWindow()
    {
        InitializeComponent();
        LstCarpetasDesborde.ItemsSource = _carpetasConDesborde;
        ItemsImagenes.ItemsSource = _imagenesActuales;

        // Timer cada 500 ms para medir RAM sin saturar el hilo de UI
        _ramTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _ramTimer.Tick += (_, _) => ActualizarRam();
        _ramTimer.Start();
        ActualizarRam();
    }

    private void ActualizarRam()
    {
        using var proc = Process.GetCurrentProcess();
        double workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
        double gcMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

        TxtRamProceso.Text = $"{workingSetMb:F1} MB";
        TxtRamGc.Text = $"{gcMb:F1} MB";
    }

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
            _ultimoPlan.Clear();
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
        TxtEstado.Text = "Escaneando carpetas e imágenes...";

        string raiz = _carpetaMadre;

        var resultados = await Task.Run(() =>
        {
            var encontrados = new List<CarpetaOverflowInfo>();

            // EnumerateDirectories evita cargar todas las rutas de golpe en un array gigante
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

            foreach (string dir in Directory.EnumerateDirectories(raiz, "*", opcionesDir))
            {
                // Ignoramos carpetas ya numeradas si son hijas directas de un desborde previo (opcional)
                var imagenes = Directory.EnumerateFiles(dir, "*.*", opcionesFile)
                    .Where(f => ExtensionesImagen.Contains(Path.GetExtension(f)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (imagenes.Count > LimitePorCarpeta)
                {
                    int subcarpetasNecesarias = (int)Math.Ceiling(imagenes.Count / (double)LimitePorCarpeta);
                    encontrados.Add(new CarpetaOverflowInfo
                    {
                        FullPath = dir,
                        RelativePath = Path.GetRelativePath(raiz, dir),
                        TotalImagenes = imagenes.Count,
                        SubcarpetasNecesarias = subcarpetasNecesarias,
                        Archivos = imagenes
                    });
                }
            }

            return encontrados.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        });

        foreach (var item in resultados)
            _carpetasConDesborde.Add(item);

        TxtEstado.Text = $"Escaneo completo. Carpetas con >{LimitePorCarpeta} imágenes: {_carpetasConDesborde.Count}";
        SetBotonesHabilitados(true);
        BtnSimular.IsEnabled = _carpetasConDesborde.Count > 0;
    }

    private async void BtnSimular_Click(object sender, RoutedEventArgs e)
    {
        if (_carpetasConDesborde.Count == 0) return;

        SetBotonesHabilitados(false);
        TxtEstado.Text = "Calculando plan de desborde (simulación)...";

        _ultimoPlan = await Task.Run(() => ConstruirPlanDesborde(_carpetasConDesborde));

        int totalSubcarpetas = _ultimoPlan.Sum(p => p.Subcarpetas.Count);
        int totalArchivos = _ultimoPlan.Sum(p => p.Subcarpetas.Sum(s => s.Archivos.Count));

        TxtEstado.Text = $"Simulación: se crearían {totalSubcarpetas} subcarpetas (1, 2, 3...) para {totalArchivos} imágenes.";
        SetBotonesHabilitados(true);
        BtnAplicar.IsEnabled = _ultimoPlan.Count > 0;
    }

    private async void BtnAplicar_Click(object sender, RoutedEventArgs e)
    {
        if (_ultimoPlan.Count == 0) return;

        bool copiar = ChkCopiarEnVezDeMover.IsChecked == true;
        string accion = copiar ? "copiar" : "mover";

        var confirm = MessageBox.Show(
            $"Se van a {accion} las imágenes a subcarpetas numeradas (1, 2, 3...) dentro de cada carpeta con más de {LimitePorCarpeta} imágenes.\n\n¿Continuar?",
            "Confirmar desborde",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        SetBotonesHabilitados(false);
        LimpiarVisor();
        TxtEstado.Text = $"Ejecutando desborde ({accion})...";

        var planActual = _ultimoPlan;

        await Task.Run(() =>
        {
            foreach (var carpeta in planActual)
            {
                foreach (var lote in carpeta.Subcarpetas)
                {
                    string destinoDir = Path.Combine(carpeta.CarpetaOrigen, lote.Indice.ToString());
                    Directory.CreateDirectory(destinoDir);

                    foreach (string origenArchivo in lote.Archivos)
                    {
                        string nombreArchivo = Path.GetFileName(origenArchivo);
                        string destinoArchivo = Path.Combine(destinoDir, nombreArchivo);

                        if (copiar)
                        {
                            File.Copy(origenArchivo, destinoArchivo, overwrite: false);
                        }
                        else
                        {
                            if (!string.Equals(origenArchivo, destinoArchivo, StringComparison.OrdinalIgnoreCase))
                                File.Move(origenArchivo, destinoArchivo, overwrite: false);
                        }
                    }
                }
            }
        });

        _ultimoPlan.Clear();
        BtnAplicar.IsEnabled = false;
        TxtEstado.Text = "Desborde finalizado. Puedes volver a escanear para verificar.";
        SetBotonesHabilitados(true);
    }

    private static List<PlanDesborde> ConstruirPlanDesborde(IEnumerable<CarpetaOverflowInfo> carpetas)
    {
        var planes = new List<PlanDesborde>();

        foreach (var carpeta in carpetas)
        {
            var subcarpetas = new List<LoteSubcarpeta>();
            int indice = 1;

            for (int i = 0; i < carpeta.Archivos.Count; i += LimitePorCarpeta)
            {
                var chunk = carpeta.Archivos.Skip(i).Take(LimitePorCarpeta).ToList();
                subcarpetas.Add(new LoteSubcarpeta
                {
                    Indice = indice++,
                    Archivos = chunk
                });
            }

            planes.Add(new PlanDesborde
            {
                CarpetaOrigen = carpeta.FullPath,
                Subcarpetas = subcarpetas
            });
        }

        return planes;
    }

    private async void LstCarpetasDesborde_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstCarpetasDesborde.SelectedItem is not CarpetaOverflowInfo seleccionada)
            return;

        LimpiarVisor();
        TxtEstado.Text = $"Cargando vista previa (primeras {LimitePorCarpeta} de {seleccionada.TotalImagenes})...";

        // Solo decodificamos las primeras 60 para mantener la RAM controlada
        var muestra = seleccionada.Archivos.Take(LimitePorCarpeta).ToList();

        var items = await Task.Run(() =>
        {
            var lista = new List<ImagenPreviewItem>(muestra.Count);
            foreach (string ruta in muestra)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(ruta);
                bmp.CacheOption = BitmapCacheOption.OnDemand;
                bmp.DecodePixelWidth = 160; // Evita cargar resoluciones gigantes en RAM
                bmp.EndInit();
                bmp.Freeze(); // Permite usar el BitmapImage de forma segura en el UI thread

                lista.Add(new ImagenPreviewItem
                {
                    FileName = Path.GetFileName(ruta),
                    Thumbnail = bmp
                });
            }
            return lista;
        });

        foreach (var item in items)
            _imagenesActuales.Add(item);

        TxtEstado.Text = $"{seleccionada.RelativePath} — {seleccionada.TotalImagenes} imágenes ({seleccionada.SubcarpetasNecesarias} subcarpetas 1..{seleccionada.SubcarpetasNecesarias})";
    }

    private void LimpiarVisor()
    {
        _imagenesActuales.Clear();
    }

    private void SetBotonesHabilitados(bool habilitado)
    {
        BtnSeleccionar.IsEnabled = habilitado;
        BtnEscanear.IsEnabled = habilitado && !string.IsNullOrWhiteSpace(_carpetaMadre);
        BtnSimular.IsEnabled = habilitado && _carpetasConDesborde.Count > 0;
        BtnAplicar.IsEnabled = habilitado && _ultimoPlan.Count > 0;
    }

    public sealed class CarpetaOverflowInfo
    {
        public string FullPath { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public int TotalImagenes { get; init; }
        public int SubcarpetasNecesarias { get; init; }
        public List<string> Archivos { get; init; } = [];
        public string Resumen => $"{TotalImagenes} imgs → {SubcarpetasNecesarias} carpetas (1..{SubcarpetasNecesarias})";
    }

    public sealed class ImagenPreviewItem
    {
        public string FileName { get; init; } = string.Empty;
        public BitmapImage? Thumbnail { get; init; }
    }

    private sealed class PlanDesborde
    {
        public string CarpetaOrigen { get; init; } = string.Empty;
        public List<LoteSubcarpeta> Subcarpetas { get; init; } = [];
    }

    private sealed class LoteSubcarpeta
    {
        public int Indice { get; init; }
        public List<string> Archivos { get; init; } = [];
    }
}