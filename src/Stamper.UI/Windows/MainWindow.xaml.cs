﻿using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Stamper.DataAccess;
using Stamper.UI.Controls;
using Stamper.UI.Events;
using Stamper.UI.Filters;
using Stamper.UI.ViewModels;
using Stamper.UI.ViewModels.Base;
using Color = System.Drawing.Color;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace Stamper.UI.Windows
{
    public partial class MainWindow : Window
    {
        private MainWindowViewModel _vm;
        private PreviewWindow _preWindow;

        private BorderControlViewModel.BorderInfo _borderInfo;
        private OverlayControlViewModel.OverlayInfo _overlayInfo;
        private Color _overlayTintColor = Color.FromArgb(0, 255, 255, 255); //Default to transparent
        private Color _borderTintColor = Color.FromArgb(0, 255, 255, 255); //Default to transparent
        private FilterMethods.TintFilterDelegate _overlayTintFilter = FilterMethods.Normal;
        private FilterMethods.TintFilterDelegate _borderTintFilter = FilterMethods.Normal;
        private FilterMethods.SpecialFilterDelegate _specialFilter = FilterMethods.None;
        private readonly DispatcherTimer _timer;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainWindowViewModel();
            DataContext = _vm;

            _vm.UpdateResolution = new RelayCommand(param =>
            {
                if (param != null) _vm.ImageResolution = int.Parse(param.ToString());
                UpdateOverlays();
            });
            _vm.ResetImageCommand = new RelayCommand(o =>
            {
                ZoomControl.Reset();
                ZoomControl_Text.Reset();
                SpecialControl._vm.RotationAngle = "0";
                SpecialControl._vm.TextRotationAngle = "0";
                if (_vm.AutoUpdatePreview) RenderImage();
            });
            _vm.OpenPreviewWindow = new RelayCommand(o => OpenPreviewWindow(null, null), o => _preWindow == null);
            _vm.UpdatePreview = new RelayCommand(o => RenderImage(), o => _preWindow != null);
            _vm.SaveToken = new RelayCommand(o => MenuItemSave_OnClick(null, null));
            _vm.LoadToken = new RelayCommand(o => MenuItemLoad_OnClick(null, null));
            _vm.UpdateZoomSpeed = new RelayCommand(o =>
            {
                _vm.ZoomSpeed = o.ToString();
                var param = Convert.ToDecimal(o.ToString(), CultureInfo.InvariantCulture);
                ZoomControl.ZoomSpeed = Convert.ToDouble(param);
            });

            _vm.Image = BitmapHelper.ConvertBitmapToImageSource(DataAccess.Properties.Resources.Splash);

            //Timer for mousewheel events.
            _timer = new DispatcherTimer();
            _timer.Tick += TimerTicked;
            _timer.Interval = TimeSpan.FromMilliseconds(200);

            CheckIfUpdateAvailable();
        }

        private async void CheckIfUpdateAvailable()
        {
            var result = await UpdateChecker.CheckForUpdate();
            if (!result.Item1) return;

            var win = new UpdateAvailableWindow(result.Item2)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            win.Show();
        }

        private void OpenPreviewWindow(object sender, RoutedEventArgs e)
        {
            _preWindow?.Close();

            var sidemargin = 20;
            var topmargin = 30;

            _preWindow = new PreviewWindow(new PreviewWindowViewModel());
            _preWindow.Height = Math.Max(164, _vm.ImageResolution) + topmargin * 2;
            _preWindow.Width = Math.Max(164, _vm.ImageResolution) + sidemargin * 2;
            _preWindow.Closed += (o, args) =>
            {
                _preWindow = null;
                _vm.OpenPreviewWindow.OnCanExecuteChanged(sender);
                _vm.UpdatePreview.OnCanExecuteChanged(sender);
            };
            _preWindow.Show();

            _vm.OpenPreviewWindow.OnCanExecuteChanged(sender);
            _vm.UpdatePreview.OnCanExecuteChanged(sender);
            RenderImage();
        }

        private Bitmap RenderVisual(Visual element)
        {
            //Setting image offset and size.
            var offsetFromTopLeft = new Point(ZoomControl.ActualWidth / 2 - RenderLocation.ActualWidth / 2, ZoomControl.ActualHeight / 2 - RenderLocation.ActualHeight / 2);
            var imageSize = new Size(_vm.ImageResolution, _vm.ImageResolution);

            //Rendering part of visual.
            var brush = new VisualBrush(element)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(offsetFromTopLeft.X, offsetFromTopLeft.Y, imageSize.Width, imageSize.Height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(new Point(0, 0), imageSize)
            };

            var renderTarget = new Rectangle { Width = imageSize.Width, Height = imageSize.Height, Fill = brush };
            renderTarget.Measure(imageSize);
            renderTarget.Arrange(new Rect(0, 0, imageSize.Width, imageSize.Height));

            var render = new RenderTargetBitmap((int)imageSize.Width, (int)imageSize.Height, 96, 96, PixelFormats.Pbgra32);
            render.Render(renderTarget);


            using (var ms = new MemoryStream())
            {
                var enc = new PngBitmapEncoder();
                var bitmapFrame = BitmapFrame.Create(render);
                enc.Frames.Add(bitmapFrame);
                enc.Save(ms);
                return BitmapHelper.ConvertToPixelFormat_32bppArgb(new Bitmap(ms));
            }
        }

        private Bitmap RenderImage()
        {
            Bitmap bitmap = RenderVisual(ZoomControl);

            //Apply the special filter.
            BitmapHelper.AddFilter(bitmap, _specialFilter);

            // Modify the rendered image.
            if (_overlayInfo != null)
            {
                var overlay = LayerSource.GetBitmapFromFile(_overlayInfo.Info.File, _vm.ImageResolution, _vm.ImageResolution);
                BitmapHelper.AddFilter(overlay, _overlayTintColor, _overlayTintFilter);
                if (!string.IsNullOrWhiteSpace(_overlayInfo.Info.Mask)) BitmapHelper.ApplyMaskToImage(overlay, LayerSource.GetBitmapFromFile(_overlayInfo.Info.Mask, _vm.ImageResolution, _vm.ImageResolution));
                BitmapHelper.AddLayerToImage(bitmap, overlay);
            }

            if (_borderInfo != null)
            {
                if (!string.IsNullOrWhiteSpace(_borderInfo.Info.Mask)) BitmapHelper.ApplyMaskToImage(bitmap, LayerSource.GetBitmapFromFile(_borderInfo.Info.Mask, _vm.ImageResolution, _vm.ImageResolution));
                var border = LayerSource.GetBitmapFromFile(_borderInfo.Info.File, _vm.ImageResolution, _vm.ImageResolution);
                BitmapHelper.AddFilter(border, _borderTintColor, _borderTintFilter);
                BitmapHelper.AddLayerToImage(bitmap, border);  //Draw the border
            }

            if (ZoomControl_Text.Visibility == Visibility.Visible)
            {
                Bitmap text = RenderVisual(ZoomControl_Text);
                BitmapHelper.AddLayerToImage(bitmap, text);
            }

            //Since we just spent time rendering the image, we might as well update the preview even if the user didn't ask for that specifically.
            _preWindow?.SetImage(bitmap, _vm.ImageResolution, _vm.ImageResolution);
            return bitmap;
        }

        private void UpdateOverlays()
        {
            if (_borderInfo != null)
            {
                //Border
                var borderImage = LayerSource.GetBitmapFromFile(_borderInfo.Info.File, _vm.ImageResolution, _vm.ImageResolution);
                BitmapHelper.AddFilter(borderImage, _borderTintColor, _borderTintFilter);

                BorderImage.Source = BitmapHelper.ConvertBitmapToImageSource(borderImage);
                BorderImage.Height = _vm.ImageResolution;
                BorderImage.Width = _vm.ImageResolution;
            }

            if (_overlayInfo != null)
            {
                //Overlay
                var overlayImage = LayerSource.GetBitmapFromFile(_overlayInfo.Info.File, _vm.ImageResolution, _vm.ImageResolution);
                BitmapHelper.AddFilter(overlayImage, _overlayTintColor, _overlayTintFilter);
                if (!string.IsNullOrWhiteSpace(_overlayInfo.Info.Mask)) BitmapHelper.ApplyMaskToImage(overlayImage, LayerSource.GetBitmapFromFile(_overlayInfo.Info.Mask, _vm.ImageResolution, _vm.ImageResolution));

                OverlayImage.Source = BitmapHelper.ConvertBitmapToImageSource(overlayImage);
                OverlayImage.Height = _vm.ImageResolution;
                OverlayImage.Width = _vm.ImageResolution;
            }

            if (_vm.AutoUpdatePreview) RenderImage();
        }

        #region Layer Selection
        private void BorderControl_OnBorderSelected(object sender, RoutedEventArgs e)
        {
            var bc = sender as BorderControl;
            var bi = bc.BorderList.SelectedItem as BorderControlViewModel.BorderInfo;
            _borderInfo = bi;
            UpdateOverlays();
        }

        private void BorderControl_OnTintSelected(object sender, RoutedEventArgs e)
        {
            var bc = sender as BorderControl;
            var color = bc.SelectedColor;
            _borderTintColor = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
            UpdateOverlays();
        }

        private void BorderControl_OnTintFilterSelected(object sender, RoutedEventArgs e)
        {
            var bc = sender as BorderControl;
            var filter = bc.FilterBox.SelectedItem as TintFilter;
            _borderTintFilter = filter.Method;
            UpdateOverlays();
        }

        private void OverlayControl_OnTintFilterSelected(object sender, RoutedEventArgs e)
        {
            var oc = sender as OverlayControl;
            var filter = oc.FilterBox.SelectedItem as TintFilter;
            _overlayTintFilter = filter.Method;
            UpdateOverlays();
        }

        private void OverlayControl_OnOverlaySelected(object sender, RoutedEventArgs e)
        {
            var oc = sender as OverlayControl;
            var oi = oc.OverlayList.SelectedItem as OverlayControlViewModel.OverlayInfo;
            _overlayInfo = oi;
            UpdateOverlays();
        }

        private void OverlayControl_OnTintSelected(object sender, RoutedEventArgs e)
        {
            var oc = sender as OverlayControl;
            var color = oc.SelectedColor;
            _overlayTintColor = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
            UpdateOverlays();
        }

        private void SpecialControl_OnFilterSelected(object sender, RoutedEventArgs e)
        {
            var gc = sender as SpecialControl;
            var specialfilter = gc.SpecialFilterBox.SelectedItem as SpecialFilter;
            if (specialfilter != null) _specialFilter = specialfilter.Method;
            UpdateOverlays();
        }

        private void SpecialControl_OnRotationChanged(object sender, RoutedEventArgs e)
        {
            int num;
            if (int.TryParse((sender as SpecialControl)._vm.RotationAngle, out num))
            {
                _vm.RotationAngle = num;
            }
            if (_vm.AutoUpdatePreview) RenderImage();
        }

        private void SpecialControl_OnTextManipulationChanged(object sender, RoutedEventArgs e)
        {
            _vm.ShowTextBorder = (sender as SpecialControl)._vm.TextManipulationShowBorder;
            _vm.ShowText = (sender as SpecialControl)._vm.TextManipulationShowText ? Visibility.Visible : Visibility.Collapsed;
            _vm.TextFont = (sender as SpecialControl).FontBox.SelectedItem as System.Windows.Media.FontFamily;
            _vm.TextColor = new SolidColorBrush((sender as SpecialControl)._vm.TextColor);

            //Convert \n to an actual newline
            var text = (sender as SpecialControl)._vm.TextContent.ToCharArray();
            var finalText = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i < text.Length - 1 && text[i + 1] == 'n')
                {
                    finalText.Append(Environment.NewLine);
                    i++;
                }
                else
                {
                    finalText.Append(text[i]);
                }
            }
            _vm.TextContent = finalText.ToString();


            int num;
            if (int.TryParse((sender as SpecialControl)._vm.TextRotationAngle, out num))
            {
                _vm.TextRotationAngle = num;
            }

            if (_vm.AutoUpdatePreview) RenderImage();
        }
        #endregion

        #region Menu
        private void MenuItemLoad_OnClick(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog()
            {
                Title = "Choose file to load",
                Filter = "Supported Images|*.jpg;*.jpeg;*.gif;*.png;*.bmp;*.tif",
                Multiselect = false
            };

            var result = dialog.ShowDialog();

            if (result != null && result.Value)
            {
                _vm.LoadExternalImage(dialog.FileName, ExternalImageType.LocalFile);
            }
        }

        private void MenuItemSave_OnClick(object sender, RoutedEventArgs e)
        {
            var image = RenderImage();

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Choose save location",
                FileName = "token",
                DefaultExt = ".png",
                AddExtension = true,
                Filter = "All Files|*.*"
            };

            var result = dialog.ShowDialog();

            if (result != null && result.Value)
            {
                if (!dialog.FileName.EndsWith(".png")) dialog.FileName = dialog.FileName + ".png";
                image.Save(dialog.FileName, ImageFormat.Png);
            }
        }

        private void MenuItemAbout_OnClick(object sender, RoutedEventArgs e)
        {
            var win = new AboutWindow
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            win.Show();
        }

        private void MenuItemRatelimiter_OnClick(object sender, RoutedEventArgs e)
        {
            var win = new RatelimitWindow
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            win.Show();
        }

        private async void MenuItemImgur_OnClick(object sender, RoutedEventArgs e)
        {
            MenuImgur.IsEnabled = false;

            var win = new UploadingWindow
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            win.Show();
            var url = await Imgur.UploadImage(RenderImage());
            win.Close();

            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show(this, "Upload failed. The upload-ratelimits have been hit, or something went wrong.", "Upload Failed");
            }
            else
            {
                Process.Start(new ProcessStartInfo(url));
            }

            MenuImgur.IsEnabled = true;
        }

        private void MenuItemCustomSize_OnClick(object sender, RoutedEventArgs e)
        {
            var win = new CustomSizeWindow
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            win.Show();

            win.Closing += (o, args) =>
            {
                int num;

                if (win.OkClicked && !string.IsNullOrWhiteSpace(win.SizeInput.Text) && int.TryParse(win.SizeInput.Text, out num))
                {
                    _vm.UpdateResolution.Execute(num);
                }
            };
        }

        private void MenuItemAddLayer_OnClick(object sender, RoutedEventArgs e)
        {
            var win = new AddLayerWindow()
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            win.Show();

            win.Closing += (o, args) =>
            {
                if (win.OkClicked)
                {
                    var layerType = (Layer.LayerType)win.LayerType.SelectedItem;

                    switch (layerType)
                    {
                        case Layer.LayerType.Border:
                            Borders.RefreshLayers();
                            break;
                        case Layer.LayerType.Overlay:
                            Overlays.RefreshLayers();
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            };
        }
        #endregion

        #region ZoomBorder
        private void ZoomControl_OnDrop(object sender, DragEventArgs e)
        {
            // Edge or Firefox will save the image locally and pass it as a local file. Chrome gives you a Html-fragment
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                try
                {
                    _vm.LoadExternalImage(((string[])e.Data.GetData(DataFormats.FileDrop))[0], ExternalImageType.LocalFile);
                }
                catch (NotSupportedException)
                {
                    MessageBox.Show(this, "File couldn't be loaded");
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.Html))
            {
                var regex = new Regex("<!--StartFragment--><img\\s.*src=\"(?<source>.*?)\".*<!--EndFragment-->");
                var match = regex.Match(e.Data.GetData(DataFormats.Html).ToString());
                if (match.Success)
                {
                    var imagesource = match.Groups["source"].Value;

                    try
                    {
                        _vm.LoadExternalImage(imagesource, ExternalImageType.WebContent);
                    }
                    catch (NotSupportedException)
                    {
                        MessageBox.Show(this, "Input couldn't be loaded");
                    }
                }
            }
        }

        private void ZoomControl_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_vm.AutoUpdatePreview) RenderImage();
        }

        private void ZoomControl_OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_vm.AutoUpdatePreview)
            {
                _timer.Stop();
                _timer.Start();
            }
        }

        private void TimerTicked(object sender, EventArgs eventArgs)
        {
            var dt = sender as DispatcherTimer;
            dt.Stop();
            RenderImage();
        }

        private void ZoomControl_Text_OnRotation(object sender, RoutedEventArgs e)
        {
            var arg = (RotationEvent)e;
            _vm.TextRotationAngle += arg.AngleDelta;
            SpecialControl._vm.TextRotationAngle = _vm.TextRotationAngle.ToString();
        }

        private void ZoomControl_OnRotation(object sender, RoutedEventArgs e)
        {
            var arg = (RotationEvent)e;
            _vm.RotationAngle += arg.AngleDelta;
            SpecialControl._vm.RotationAngle = _vm.RotationAngle.ToString();
        }
        #endregion

        private void MenuItemLoadInstructions_OnClick(object sender, RoutedEventArgs e)
        {
            var window = new InstructionsWindow
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            window.Show();
        }
    }
}
