﻿using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickLook.Plugin.PDFViewer
{
    /// <summary>
    ///     Interaction logic for PdfViewer.xaml
    /// </summary>
    public partial class PdfViewerControl : UserControl, INotifyPropertyChanged, IDisposable
    {
        private Point? _dragInitPos;
        private PreviewMouseWheelMonitor _whellMonitor;

        public PdfViewerControl()
        {
            InitializeComponent();

            DataContext = this;

            PageIds = new ObservableCollection<int>();
        }

        public ObservableCollection<int> PageIds { get; set; }

        public PdfFile PdfHandleForThumbnails { get; private set; }

        public PdfFile PdfHandle { get; private set; }

        public bool PdfLoaded { get; private set; }

        public double ZoomFactor { get; set; }

        public double MinZoomFactor { get; set; }

        public int TotalPages => PdfHandle.TotalPages;

        public int CurrentPage
        {
            get => listThumbnails.SelectedIndex;
            set
            {
                listThumbnails.SelectedIndex = value;
                listThumbnails.ScrollIntoView(listThumbnails.SelectedItem);

                CurrentPageChanged?.Invoke(this, new EventArgs());
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            _whellMonitor?.Dispose();
            PdfHandleForThumbnails?.Dispose();
            PdfHandle?.Dispose();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        ~PdfViewerControl()
        {
            Dispose();
        }

        public event EventHandler CurrentPageChanged;

        private void NavigatePage(object sender, MouseWheelEventArgs e)
        {
            if (!PdfLoaded)
                return;

            if (Keyboard.Modifiers != ModifierKeys.None)
                return;

            if (e.Delta > 0) // up
            {
                if (pageViewPanel.VerticalOffset != 0) return;

                PrevPage();
                e.Handled = true;
            }
            else // down
            {
                if (pageViewPanel.VerticalOffset != pageViewPanel.ScrollableHeight) return;

                NextPage();
                e.Handled = true;
            }
        }

        private void NextPage()
        {
            if (CurrentPage < PdfHandle.TotalPages - 1)
            {
                CurrentPage++;
                pageViewPanel.ScrollToTop();
            }
        }

        private void PrevPage()
        {
            if (CurrentPage > 0)
            {
                CurrentPage--;
                pageViewPanel.ScrollToBottom();
            }
        }

        private void ReRenderCurrentPageLowQuality(double viewZoom, bool fromCenter)
        {
            if (pageViewPanelImage.Source == null)
                return;

            var position = fromCenter
                ? new Point(pageViewPanelImage.Source.Width / 2, pageViewPanelImage.Source.Height / 2)
                : Mouse.GetPosition(pageViewPanelImage);

            pageViewPanelImage.LayoutTransform = new ScaleTransform(viewZoom, viewZoom);

            pageViewPanel.InvalidateMeasure();

            // critical for calcuating offset
            pageViewPanel.ScrollToHorizontalOffset(0);
            pageViewPanel.ScrollToVerticalOffset(0);
            UpdateLayout();

            var offset = pageViewPanelImage.TranslatePoint(position, pageViewPanel) - Mouse.GetPosition(pageViewPanel);
            pageViewPanel.ScrollToHorizontalOffset(offset.X);
            pageViewPanel.ScrollToVerticalOffset(offset.Y);
            UpdateLayout();
        }


        private void ReRenderCurrentPage()
        {
            if (!PdfLoaded)
                return;

            var image = PdfHandle.GetPage(CurrentPage, ZoomFactor).ToBitmapSource();

            pageViewPanelImage.Source = image;
            pageViewPanelImage.Width = pageViewPanelImage.Source.Width;
            pageViewPanelImage.Height = pageViewPanelImage.Source.Height;

            // reset view zoom factor
            pageViewPanelImage.LayoutTransform = new ScaleTransform();

            pageViewPanel.InvalidateMeasure();

            GC.Collect();
        }

        private void UpdatePageViewWhenSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!PdfLoaded)
                return;

            if (CurrentPage == -1)
                return;

            CurrentPageChanged?.Invoke(this, new EventArgs());

            ReRenderCurrentPage();
        }

        private void ZoomToFit()
        {
            if (!PdfLoaded)
                return;

            var size = PdfHandle.GetPageSize(CurrentPage, 1d);

            var factor = Math.Min(pageViewPanel.ActualWidth / size.Width, pageViewPanel.ActualHeight / size.Height);

            ZoomFactor = factor;
            MinZoomFactor = factor;

            ReRenderCurrentPage();
        }

        public static Size GetDesiredControlSizeByFirstPage(string path)
        {
            var tempHandle = new PdfFile(path);

            var size = tempHandle.GetPageSize(0, 1d);
            tempHandle.Dispose();

            size.Width += /*listThumbnails.ActualWidth*/ 150;

            return size;
        }

        public void LoadPdf(string path)
        {
            PageIds.Clear();
            _whellMonitor?.Dispose();

            PdfHandleForThumbnails = new PdfFile(path);
            PdfHandle = new PdfFile(path);
            PdfLoaded = true;

            // fill thumbnails list
            Enumerable.Range(0, PdfHandle.TotalPages).ForEach(PageIds.Add);
            OnPropertyChanged(nameof(PageIds));

            CurrentPage = 0;

            // calculate zoom factor for first page
            ZoomToFit();

            // register events
            listThumbnails.SelectionChanged += UpdatePageViewWhenSelectionChanged;
            //pageViewPanel.SizeChanged += ReRenderCurrentPageWhenSizeChanged;

            pageViewPanel.PreviewMouseWheel += NavigatePage;
            StartMouseWhellDelayedZoomMonitor(pageViewPanel);

            pageViewPanel.PreviewMouseLeftButtonDown += DragScrollStart;
            pageViewPanel.PreviewMouseMove += DragScrolling;
        }

        private void DragScrolling(object sender, MouseEventArgs e)
        {
            if (!_dragInitPos.HasValue)
                return;

            if (e.LeftButton == MouseButtonState.Released)
            {
                _dragInitPos = null;
                return;
            }

            e.Handled = true;

            var delta = _dragInitPos.Value - e.GetPosition(pageViewPanel);

            pageViewPanel.ScrollToHorizontalOffset(delta.X);
            pageViewPanel.ScrollToVerticalOffset(delta.Y);
        }

        private void DragScrollStart(object sender, MouseButtonEventArgs e)
        {
            _dragInitPos = e.GetPosition(pageViewPanel);
            var temp = _dragInitPos.Value; // Point is a type value
            temp.Offset(pageViewPanel.HorizontalOffset, pageViewPanel.VerticalOffset);
            _dragInitPos = temp;
        }

        private void StartMouseWhellDelayedZoomMonitor(UIElement ui)
        {
            if (_whellMonitor == null)
                _whellMonitor = new PreviewMouseWheelMonitor(ui, 100);

            var newZoom = 1d;
            var scrolling = false;

            _whellMonitor.PreviewMouseWheelStarted += (sender, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                    return;

                newZoom = ZoomFactor;
                scrolling = true;
            };
            _whellMonitor.PreviewMouseWheel += (sender, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                    return;

                e.Handled = true;

                newZoom = newZoom + e.Delta / 120 * 0.1;

                newZoom = Math.Max(newZoom, MinZoomFactor);
                newZoom = Math.Min(newZoom, 3);

                ReRenderCurrentPageLowQuality(newZoom / ZoomFactor, false);
            };
            _whellMonitor.PreviewMouseWheelStopped += (sender, e) =>
            {
                if (!scrolling)
                    return;

                ZoomFactor = newZoom;
                ReRenderCurrentPage();
                scrolling = false;
            };
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}