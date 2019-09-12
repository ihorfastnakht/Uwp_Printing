using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.UI.Xaml.Navigation;
using Microsoft.Toolkit.Uwp.Helpers;
using System.Threading.Tasks;
using Windows.UI.Popups;
using Windows.Graphics.Printing;
using Windows.UI.Xaml.Printing;
using Windows.ApplicationModel.Core;
using Windows.UI.Xaml.Shapes;

namespace App22_Printing
{

    internal class CustPrintHelperStateBag
    {
        public HorizontalAlignment HorizontalAlignment { get; set; }

        public VerticalAlignment VerticalAlignment { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public Thickness Margin { get; set; }

        public void Capture(FrameworkElement element)
        {
            HorizontalAlignment = element.HorizontalAlignment;
            VerticalAlignment = element.VerticalAlignment;
            Width = element.Width;
            Height = element.Height;
            Margin = element.Margin;
        }

        public void Restore(FrameworkElement element)
        {
            DispatcherHelper.ExecuteOnUIThreadAsync(() =>
            {
                element.HorizontalAlignment = HorizontalAlignment;
                element.VerticalAlignment = VerticalAlignment;
                element.Width = Width;
                element.Height = Height;
                element.Margin = Margin;
            });
        }
    }

    public class CustPrintHelper : IDisposable
    {
        public event Action OnPrintSucceeded;

        public event Action OnPrintFailed;

        public event Action OnPrintCanceled;

        public event Action<List<FrameworkElement>> OnPreviewPagesCreated;

        public double ApplicationContentMarginLeft { get; set; } = 0.03;

        public double ApplicationContentMarginTop { get; set; } = 0.03;

        private PrintDocument _printDocument;

        private IPrintDocumentSource _printDocumentSource;

        private List<FrameworkElement> _printPreviewPages;

        private Canvas _printCanvas;

        private Panel _canvasContainer;
        private string _printTaskName;
        private Dictionary<FrameworkElement, CustPrintHelperStateBag> _stateBags = new Dictionary<FrameworkElement, CustPrintHelperStateBag>();
        private bool _directPrint = false;

        private List<FrameworkElement> _elementsToPrint;

        private PrintHelperOptions _printHelperOptions;

        private PrintHelperOptions _defaultPrintHelperOptions;

        public CustPrintHelper(Panel canvasContainer, PrintHelperOptions defaultPrintHelperOptions = null)
        {
            _canvasContainer = canvasContainer ?? throw new ArgumentNullException();
            _canvasContainer.RequestedTheme = ElementTheme.Light;

            _printPreviewPages = new List<FrameworkElement>();
            _printCanvas = new Canvas();
            _printCanvas.Opacity = 0;

            _elementsToPrint = new List<FrameworkElement>();

            _defaultPrintHelperOptions = defaultPrintHelperOptions ?? new PrintHelperOptions();

            RegisterForPrinting();
        }

        public void AddFrameworkElementToPrint(FrameworkElement element)
        {
            if (element.Parent != null)
            {
                throw new ArgumentException("Printable elements cannot have a parent.");
            }

            _elementsToPrint.Add(element);
        }

        public void RemoveFrameworkElementToPrint(FrameworkElement element)
        {
            _elementsToPrint.Remove(element);
        }

        public void ClearListOfPrintableFrameworkElements()
        {
            _elementsToPrint.Clear();
        }

        public async Task ShowPrintUIAsync(string printTaskName, bool directPrint = false)
        {
            this._directPrint = directPrint;

            PrintManager printMan = PrintManager.GetForCurrentView();
            printMan.PrintTaskRequested += PrintTaskRequested;

            // Launch print process
            _printTaskName = printTaskName;
            await Task.Delay(TimeSpan.FromSeconds(1));
            await PrintManager.ShowPrintUIAsync();
        }

        public Task ShowPrintUIAsync(string printTaskName, PrintHelperOptions printHelperOptions, bool directPrint = false)
        {
            _printHelperOptions = printHelperOptions;

            return ShowPrintUIAsync(printTaskName, directPrint);
        }

        public void Dispose()
        {
            if (_printDocument == null)
            {
                return;
            }

            _printCanvas = null;
            DispatcherHelper.ExecuteOnUIThreadAsync(() =>
            {
                _printDocument.Paginate -= CreatePrintPreviewPages;
                _printDocument.GetPreviewPage -= GetPrintPreviewPage;
                _printDocument.AddPages -= AddPrintPages;
            });
        }

        private void RegisterForPrinting()
        {
            _printDocument = new PrintDocument();
            _printDocumentSource = _printDocument.DocumentSource;
            _printDocument.Paginate += CreatePrintPreviewPages;
            _printDocument.GetPreviewPage += GetPrintPreviewPage;
            _printDocument.AddPages += AddPrintPages;
        }

        private async Task DetachCanvas()
        {
            if (!_directPrint)
            {
                await DispatcherHelper.ExecuteOnUIThreadAsync(() =>
                {
                    _canvasContainer.Children.Remove(_printCanvas);
                    _printCanvas.Children.Clear();
                });
            }

            _stateBags.Clear();

            // Clear the cache of preview pages
            await ClearPageCache();

            // Remove the handler for printing initialization.
            PrintManager printMan = PrintManager.GetForCurrentView();
            printMan.PrintTaskRequested -= PrintTaskRequested;
        }

        private void PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs e)
        {
            PrintTask printTask = null;
            printTask = e.Request.CreatePrintTask(_printTaskName, async sourceRequested =>
            { 
                ApplyPrintSettings(printTask);

                await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, async () =>
                {
                    _printDocument.AddPagesComplete();
                });

                // Print Task event handler is invoked when the print job is completed.
                printTask.Completed += async (s, args) =>
                {
                    // Notify the user when the print operation fails.
                    await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, async () =>
                    {
                        foreach (var element in _stateBags.Keys)
                        {
                            _stateBags[element].Restore(element);
                        }
                        _stateBags.Clear();
                        _canvasContainer.RequestedTheme = ElementTheme.Default;
                        await DetachCanvas();

                        switch (args.Completion)
                        {
                            case PrintTaskCompletion.Failed:
                                {
                                    OnPrintFailed?.Invoke();
                                    break;
                                }
                            case PrintTaskCompletion.Canceled:
                                {
                                    OnPrintCanceled?.Invoke();
                                    break;
                                }
                            case PrintTaskCompletion.Submitted:
                                {
                                    OnPrintSucceeded?.Invoke();
                                    break;
                                }
                        }
                    });
                };

                sourceRequested.SetSource(_printDocumentSource);
            });
        }

        private void ApplyPrintSettings(PrintTask printTask)
        {
            _printHelperOptions = _printHelperOptions ?? _defaultPrintHelperOptions;

            IEnumerable<string> displayedOptionsToAdd = _printHelperOptions.DisplayedOptions;

            if (!_printHelperOptions.ExtendDisplayedOptions)
            {
                printTask.Options.DisplayedOptions.Clear();
            }

            foreach (var displayedOption in displayedOptionsToAdd)
            {
                if (!printTask.Options.DisplayedOptions.Contains(displayedOption))
                {
                    printTask.Options.DisplayedOptions.Add(displayedOption);
                }
            }

            if (printTask.Options.Binding != PrintBinding.NotAvailable)
            {
                printTask.Options.Binding = _printHelperOptions.Binding == PrintBinding.Default ? printTask.Options.Binding : _printHelperOptions.Binding;
            }

            if (printTask.Options.Bordering != PrintBordering.NotAvailable)
            {
                printTask.Options.Bordering = _printHelperOptions.Bordering == PrintBordering.Default ? printTask.Options.Bordering : _printHelperOptions.Bordering;
            }

            if (printTask.Options.MediaType != PrintMediaType.NotAvailable)
            {
                printTask.Options.MediaType = _printHelperOptions.MediaType == PrintMediaType.Default ? printTask.Options.MediaType : _printHelperOptions.MediaType;
            }

            if (printTask.Options.MediaSize != PrintMediaSize.NotAvailable)
            {
                printTask.Options.MediaSize = _printHelperOptions.MediaSize == PrintMediaSize.Default ? printTask.Options.MediaSize : _printHelperOptions.MediaSize;
            }

            if (printTask.Options.HolePunch != PrintHolePunch.NotAvailable)
            {
                printTask.Options.HolePunch = _printHelperOptions.HolePunch == PrintHolePunch.Default ? printTask.Options.HolePunch : _printHelperOptions.HolePunch;
            }

            if (printTask.Options.Duplex != PrintDuplex.NotAvailable)
            {
                printTask.Options.Duplex = _printHelperOptions.Duplex == PrintDuplex.Default ? printTask.Options.Duplex : _printHelperOptions.Duplex;
            }

            if (printTask.Options.ColorMode != PrintColorMode.NotAvailable)
            {
                printTask.Options.ColorMode = _printHelperOptions.ColorMode == PrintColorMode.Default ? printTask.Options.ColorMode : _printHelperOptions.ColorMode;
            }

            if (printTask.Options.Collation != PrintCollation.NotAvailable)
            {
                printTask.Options.Collation = _printHelperOptions.Collation == PrintCollation.Default ? printTask.Options.Collation : _printHelperOptions.Collation;
            }

            if (printTask.Options.PrintQuality != PrintQuality.NotAvailable)
            {
                printTask.Options.PrintQuality = _printHelperOptions.PrintQuality == PrintQuality.Default ? printTask.Options.PrintQuality : _printHelperOptions.PrintQuality;
            }

            if (printTask.Options.Staple != PrintStaple.NotAvailable)
            {
                printTask.Options.Staple = _printHelperOptions.Staple == PrintStaple.Default ? printTask.Options.Staple : _printHelperOptions.Staple;
            }

            if (printTask.Options.Orientation != PrintOrientation.NotAvailable)
            {
                printTask.Options.Orientation = _printHelperOptions.Orientation == PrintOrientation.Default ? printTask.Options.Orientation : _printHelperOptions.Orientation;
            }
            _printHelperOptions = null;
        }

        private async void CreatePrintPreviewPages(object sender, PaginateEventArgs e)
        {
            // Get the PrintTaskOptions
            PrintTaskOptions printingOptions = e.PrintTaskOptions;

            // Get the page description to determine how big the page is
            PrintPageDescription pageDescription = printingOptions.GetPageDescription(0);

            if (_directPrint)
            {
                _canvasContainer.RequestedTheme = ElementTheme.Light;
                foreach (FrameworkElement element in this._canvasContainer.Children)
                {
                    _printPreviewPages.Add(element);
                }
            }
            else
            {
                // Attach the canvas
                if (!_canvasContainer.Children.Contains(_printCanvas))
                {
                    _canvasContainer.Children.Add(_printCanvas);
                }

                _canvasContainer.RequestedTheme = ElementTheme.Light;

                // Clear the cache of preview pages
                await ClearPageCache();

                // Clear the print canvas of preview pages
                _printCanvas.Children.Clear();

                var printPageTasks = new List<Task>();
                foreach (var element in _elementsToPrint)
                {
                    printPageTasks.Add(AddOnePrintPreviewPage(element, pageDescription));
                }

                await Task.WhenAll(printPageTasks);
            }

            OnPreviewPagesCreated?.Invoke(_printPreviewPages);

            PrintDocument printDoc = (PrintDocument)sender;

            // Report the number of preview pages created
            //_printCanvas.InvalidateMeasure();
            _printCanvas.UpdateLayout();
            printDoc.SetPreviewPageCount(_printPreviewPages.Count, PreviewPageCountType.Intermediate);
            printDoc.SetPreviewPage(1, _printPreviewPages[0]);
        }

        private void GetPrintPreviewPage(object sender, GetPreviewPageEventArgs e)
        {
            PrintDocument printDoc = (PrintDocument)sender;
            if (_printPreviewPages.Count() > 0)
            {
                var page = _printPreviewPages[e.PageNumber - 1];
                printDoc.SetPreviewPage(e.PageNumber, page);
            }
        }

        private void AddPrintPages(object sender, AddPagesEventArgs e)
        {
            // Loop over all of the preview pages and add each one to add each page to be printed
            for (int i = 0; i < _printPreviewPages.Count; i++)
            {
                // We should have all pages ready at this point...
                _printDocument.AddPage(_printPreviewPages[i]);
            }
            PrintDocument printDoc = (PrintDocument)sender;

            // Indicate that all of the print pages have been provided
            printDoc.AddPagesComplete();
        }

        private Task AddOnePrintPreviewPage(FrameworkElement element, PrintPageDescription printPageDescription)
        {
            var page = new Page();

            // Save state
            if (!_stateBags.ContainsKey(element))
            {
                var stateBag = new CustPrintHelperStateBag();
                stateBag.Capture(element);
                _stateBags.Add(element, stateBag);
            }

            // Set "paper" width
            page.Width = printPageDescription.PageSize.Width;
            page.Height = printPageDescription.PageSize.Height;

            // Get the margins size
            double marginWidth = Math.Max(printPageDescription.PageSize.Width - printPageDescription.ImageableRect.Width, printPageDescription.PageSize.Width * ApplicationContentMarginLeft * 2);
            double marginHeight = Math.Max(printPageDescription.PageSize.Height - printPageDescription.ImageableRect.Height, printPageDescription.PageSize.Height * ApplicationContentMarginTop * 2);

            // Set up the "printable area" on the "paper"
            element.VerticalAlignment = VerticalAlignment.Top;
            element.HorizontalAlignment = HorizontalAlignment.Left;

            if (element.Width > element.Height)
            {
                var newWidth = page.Width - marginWidth;

                element.Height = element.Height * (newWidth / element.Width);
                element.Width = newWidth;
            }
            else
            {
                var newHeight = page.Height - marginHeight;

                element.Width = element.Width * (newHeight / element.Height);
                element.Height = newHeight;
            }

            element.Margin = new Thickness(marginWidth / 2, marginHeight / 2, marginWidth / 2, marginHeight / 2);
            page.Content = element;

            return DispatcherHelper.ExecuteOnUIThreadAsync(
                () =>
                {
                    // Add the (newly created) page to the print canvas which is part of the visual tree and force it to go
                    // through layout so that the linked containers correctly distribute the content inside them.
                    _printCanvas.Children.Add(page);
                    _printCanvas.UpdateLayout();
                    _printCanvas.InvalidateMeasure();

                    // Add the page to the page preview collection
                    _printPreviewPages.Add(page);
                }, Windows.UI.Core.CoreDispatcherPriority.High);
        }

        private Task ClearPageCache()
        {
            return DispatcherHelper.ExecuteOnUIThreadAsync(() =>
            {
                if (!_directPrint)
                {
                    foreach (Page page in _printPreviewPages)
                    {
                        page.Content = null;
                    }
                }

                _printPreviewPages.Clear();
            });
        }
    }


    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();
        }

        private async void Print_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();

            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".html");

            var file = await picker.PickSingleFileAsync();

            var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);

            var dataReader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
            string content = dataReader.ReadString(buffer.Length);
            

            WebView.NavigateToString(content);

            WebView.LoadCompleted += WebView_LoadCompleted; ;
        }

        CustPrintHelper _printHelper;

        private async void WebView_LoadCompleted(object sender, NavigationEventArgs e)
        {
            _printHelper = new CustPrintHelper(Canvas);
            _printHelper.OnPreviewPagesCreated += _printHelper_OnPreviewPagesCreated;
            _printHelper.OnPrintCanceled += PrintHelper_OnPrintCanceled;
            _printHelper.OnPrintFailed += PrintHelper_OnPrintFailed;
            _printHelper.OnPrintSucceeded += PrintHelper_OnPrintSucceeded;

            var pages = await GetWebPages((sender as WebView), new Size(794, 1122));

            PopulateItemsControl((sender as WebView));

            foreach (var s in pages)
            {
                _printHelper.AddFrameworkElementToPrint(s);
            }

            await _printHelper.ShowPrintUIAsync("Windows Community Toolkit Sample App");
        }

        private async void PopulateItemsControl(WebView webView)
        {
            var pages = await GetWebPages(webView, new Size(794, 1122));
            Container.ItemsSource = pages;
        }

        private void _printHelper_OnPreviewPagesCreated(List<FrameworkElement> obj)
        {
        }

        private async void PrintHelper_OnPrintSucceeded()
        {
            var dialog = new MessageDialog("Printing done.");

            await dialog.ShowAsync();

            ReleasePrinterHelper();
        }

        private async void PrintHelper_OnPrintFailed()
        {
            var dialog = new MessageDialog("Printing failed.");
            await dialog.ShowAsync();

            ReleasePrinterHelper();
        }

        private void PrintHelper_OnPrintCanceled()
        {
            ReleasePrinterHelper();
        }

        private void ReleasePrinterHelper()
        {
            if (_printHelper != null)
            {
                _printHelper.OnPrintSucceeded -= PrintHelper_OnPrintSucceeded;
                _printHelper.OnPrintFailed -= PrintHelper_OnPrintFailed;
                _printHelper.OnPrintCanceled -= PrintHelper_OnPrintCanceled;
                _printHelper.OnPreviewPagesCreated -= _printHelper_OnPreviewPagesCreated;
                _printHelper.Dispose();
                _printHelper = null;
            }
        }

        async Task<List<Rectangle>> GetWebPages(WebView webView, Size page)
        {
            // ask the content its width
            var _WidthString = await webView.InvokeScriptAsync("eval",
                new[] { "document.body.scrollWidth.toString()" });
            int _ContentWidth;
            if (!int.TryParse(_WidthString, out _ContentWidth))
                throw new Exception(string.Format("failure/width:{0}", _WidthString));
            webView.Width = _ContentWidth;

            // ask the content its height
            var _HeightString = await webView.InvokeScriptAsync("eval",
                new[] { "document.body.scrollHeight.toString()" });
            int _ContentHeight;
            if (!int.TryParse(_HeightString, out _ContentHeight))
                throw new Exception(string.Format("failure/height:{0}", _HeightString));
            webView.Height = _ContentHeight;

            // how many pages will there be?
            var _Scale = page.Width / _ContentWidth;
            var _ScaledHeight = (_ContentHeight * _Scale);
            var _PageCount = (int) Math.Round((double)_ScaledHeight / page.Height, MidpointRounding.AwayFromZero);
            //PageCount = _PageCount + ((_PageCount > (int)_PageCount) ? 1 : 0);

            // create the pages
            var _Pages = new List<Windows.UI.Xaml.Shapes.Rectangle>();
            for (int i = 0; i < (int)_PageCount; i++)
            {
                var _TranslateY = -page.Height * i;
                var _Page = new Windows.UI.Xaml.Shapes.Rectangle
                {
                    Height = page.Height,
                    Width = page.Width,
                    Margin = new Windows.UI.Xaml.Thickness(5),
                    Tag = new Windows.UI.Xaml.Media.TranslateTransform { Y = _TranslateY },
                };
                _Page.Loaded += async (s, e) =>
                {
                     await DispatcherHelper.ExecuteOnUIThreadAsync(async() =>
                    {
                        var _Rectangle = s as Windows.UI.Xaml.Shapes.Rectangle;
                        var _Brush = await GetWebViewBrush(webView);
                        _Brush.Stretch = Windows.UI.Xaml.Media.Stretch.UniformToFill;
                        _Brush.AlignmentY = Windows.UI.Xaml.Media.AlignmentY.Top;
                        _Brush.Transform = _Rectangle.Tag as Windows.UI.Xaml.Media.TranslateTransform;
                        _Rectangle.Fill = _Brush;
                    });
                };
                _Pages.Add(_Page);
            }
            return _Pages;
        }

        async Task<WebViewBrush> GetWebViewBrush(WebView webView)
        {
            // resize width to content
            var _OriginalWidth = webView.Width;
            var _WidthString = await webView.InvokeScriptAsync("eval",
                new[] { "document.body.scrollWidth.toString()" });
            int _ContentWidth;
            if (!int.TryParse(_WidthString, out _ContentWidth))
                throw new Exception(string.Format("failure/width:{0}", _WidthString));
            webView.Width = _ContentWidth;

            // resize height to content
            var _OriginalHeight = webView.Height;
            var _HeightString = await webView.InvokeScriptAsync("eval",
                new[] { "document.body.scrollHeight.toString()" });
            int _ContentHeight;
            if (!int.TryParse(_HeightString, out _ContentHeight))
                throw new Exception(string.Format("failure/height:{0}", _HeightString));
            webView.Height = _ContentHeight;

            // create brush
            var _OriginalVisibilty = webView.Visibility;
            webView.Visibility = Windows.UI.Xaml.Visibility.Visible;
            var _Brush = new WebViewBrush
            {
                SourceName = webView.Name,
                Stretch = Windows.UI.Xaml.Media.Stretch.Uniform
            };
            _Brush.Redraw();
            await Task.Delay(10);
            // reset, return
            webView.Width = _OriginalWidth;
            webView.Height = _OriginalHeight;
            webView.Visibility = _OriginalVisibilty;
            return _Brush;
        }
    }
}
