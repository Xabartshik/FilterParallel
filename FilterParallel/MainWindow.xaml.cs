using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using System.Globalization;

namespace FilterParallel
{
    public partial class MainWindow : Window
    {
        private BitmapSource originalImage;
        private BitmapSource filteredImage;
        private List<double[,]> presetFilters = new List<double[,]>();

        public MainWindow()
        {
            InitializeComponent();
            InitializePresetFilters();
            this.Loaded += MainWindow_Loaded; // Добавляем обработчик загрузки окна
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Инициализируем видимость панелей после загрузки окна
            FilterTypeRadioButton_Checked(null, null);
        }

        private void InitializePresetFilters()
        {
            // Гауссовский фильтр 3x3
            presetFilters.Add(new double[,]
            {
                {1, 2, 1},
                {2, 4, 2},
                {1, 2, 1}
            });

            // Фильтр резкости 3x3
            presetFilters.Add(new double[,]
            {
                {0, -1, 0},
                {-1, 5, -1},
                {0, -1, 0}
            });

            // Фильтр "Размытие" 5x5
            presetFilters.Add(new double[,]
            {
                {1, 1, 1, 1, 1},
                {1, 1, 1, 1, 1},
                {1, 1, 1, 1, 1},
                {1, 1, 1, 1, 1},
                {1, 1, 1, 1, 1}
            });

            PresetFilterComboBox.Items.Add("Гауссовский фильтр 3x3");
            PresetFilterComboBox.Items.Add("Фильтр резкости 3x3");
            PresetFilterComboBox.Items.Add("Размытие 5x5");
            PresetFilterComboBox.SelectedIndex = 0;
        }

        private void LoadImageButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Image files (*.jpg, *.jpeg, *.png)|*.jpg;*.jpeg;*.png"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    originalImage = new BitmapImage(new Uri(openFileDialog.FileName));
                    filteredImage = originalImage;
                    OriginalImage.Source = originalImage;
                    FilteredImage.Source = filteredImage;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка загрузки изображения: {ex.Message}");
                }
            }
        }

        private async void ApplyFilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (originalImage == null)
            {
                MessageBox.Show("Пожалуйста, загрузите изображение сначала.");
                return;
            }

            ApplyFilterButton.IsEnabled = false;

            try
            {
                double[,] filter;
                double factor;

                if (ManualRadioButton.IsChecked == true)
                {
                    try
                    {
                        int size = int.Parse(MatrixSizeTextBox.Text) + 1;
                        factor = double.Parse(FactorTextBox.Text);

                        filter = new double[size, size];
                        string[] rows = FilterMatrixTextBox.Text.Split('\n');

                        for (int i = 0; i < size; i++)
                        {
                            string[] values = rows[i].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            for (int j = 0; j < size; j++)
                            {
                                filter[i, j] = double.Parse(values[j]);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка ввода данных фильтра: {ex.Message}");
                        return;
                    }
                }
                else
                {
                    int selectedIndex = PresetFilterComboBox.SelectedIndex;
                    filter = presetFilters[selectedIndex];
                    factor = 1.0 / GetFilterSum(filter);
                }

                // Конвертируем в Bitmap
                Bitmap bitmap = BitmapSourceToBitmap(originalImage);

                // Применяем фильтр с отображением прогресса
                filteredImage = BitmapToBitmapSource(await Task.Run(() =>
                    ApplyFilterParallelPool(bitmap, filter, factor)));

                // Отображаем окончательный результат
                FilteredImage.Source = filteredImage;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}");
            }
            finally
            {
                ApplyFilterButton.IsEnabled = true;
            }
        }

        //Параллельное применение фильтра к Bitmap изображения
        private Bitmap ApplyFilterParallel(Bitmap sourceBitmap, double[,] filter, double factor)
        {
            int filterSize = filter.GetLength(0);
            int filterOffset = filterSize / 2;
            int width = sourceBitmap.Width;
            int height = sourceBitmap.Height;

            // Создаем копии данных изображения для безопасного доступа из нескольких потоков. lockBits Блокирует объект Bitmap (растровое изображение) в системной памяти, позволяя напрямую работать с его пиксельными данными.
            //https://learn.microsoft.com/ru-ru/dotnet/api/system.drawing.bitmap.lockbits?view=windowsdesktop-9.0&viewFallbackFrom=dotnet-plat-ext-8.0
            BitmapData sourceData = sourceBitmap.LockBits(
                new Rectangle(0, 0, width, height), //Область изображения для блокировки (откуда и до куда)
                ImageLockMode.ReadOnly, //Только чтение 
                PixelFormat.Format32bppArgb);//Формат пикселей (8 на каждый)

            // Создаем массив для хранения пикселей
            byte[] sourcePixels = new byte[width * height * 4];
            //Копирует данные из управляемого массива в указатель неуправляемой памяти или из указателя неуправляемой памяти в управляемый массив.
            /*
             При вызове Bitmap.LockBits() система выделяет неуправляемую память под пиксели (в формате PixelFormat.Format32bppArgb — 4 байта на пиксель: B, G, R, A).
            Scan0 возвращает указатель (IntPtr) на эту память. Это нужно ради получения информации о пикселях, ибо получение цвета GetPixel пикселя не получилось распараллелить -- возвращается
            ошибка, что объект уже используется
             */
            Marshal.Copy(sourceData.Scan0, sourcePixels, 0, sourcePixels.Length);
            sourceBitmap.UnlockBits(sourceData);

            // Создаем массив для результата
            byte[] resultPixels = new byte[width * height * 4];
            //Parallel.For автоматически распределяет строки (y) между потоками.
            //MaxDegreeOfParallelism = Environment.ProcessorCount ограничивает число потоков количеством ядер CPU.
            ParallelOptions parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            //Каждый поток пишет в свою область (по y)
            Parallel.For(0, height, parallelOptions, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    double red = 0.0, green = 0.0, blue = 0.0;

                    for (int fy = 0; fy < filterSize; fy++)
                    {
                        for (int fx = 0; fx < filterSize; fx++)
                        {
                            int imageX = x + fx - filterOffset;
                            int imageY = y + fy - filterOffset;
                            //метод, который возвращает значение, ограниченное указанными минимумом и максимумом.
                            imageX = Math.Clamp(imageX, 0, width - 1);
                            imageY = Math.Clamp(imageY, 0, height - 1);

                            // Читаем пиксель из массива
                            int sourceIndex = (imageY * width + imageX) * 4;
                            blue += sourcePixels[sourceIndex] * filter[fy, fx];
                            green += sourcePixels[sourceIndex + 1] * filter[fy, fx];
                            red += sourcePixels[sourceIndex + 2] * filter[fy, fx];
                        }
                    }

                    red = Math.Clamp(red * factor, 0, 255);
                    green = Math.Clamp(green * factor, 0, 255);
                    blue = Math.Clamp(blue * factor, 0, 255);

                    // Записываем результат
                    int resultIndex = (y * width + x) * 4;
                    resultPixels[resultIndex] = (byte)blue;
                    resultPixels[resultIndex + 1] = (byte)green;
                    resultPixels[resultIndex + 2] = (byte)red;
                    resultPixels[resultIndex + 3] = 255; // Альфа-канал
                }
            });

            // Копируем результат обратно в Bitmap
            BitmapData resultData = sourceBitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            Marshal.Copy(resultPixels, 0, resultData.Scan0, resultPixels.Length);
            sourceBitmap.UnlockBits(resultData);

            return sourceBitmap;
        }

        private Bitmap ApplyFilterParallelPool(Bitmap sourceBitmap, double[,] filter, double factor)
        {
            int filterSize = filter.GetLength(0);
            int filterOffset = filterSize / 2;
            int width = sourceBitmap.Width;
            int height = sourceBitmap.Height;

            // Блокируем биты исходного изображения
            BitmapData sourceData = sourceBitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            // Копируем пиксели в массив
            byte[] sourcePixels = new byte[width * height * 4];
            Marshal.Copy(sourceData.Scan0, sourcePixels, 0, sourcePixels.Length);
            sourceBitmap.UnlockBits(sourceData);

            // Создаем массив для результата
            byte[] resultPixels = new byte[width * height * 4];

            // Используем счетчик событий для синхронизации
            using (CountdownEvent countdownEvent = new CountdownEvent(height))
            {
                for (int y = 0; y < height; y++)
                {
                    /*
                     Переменная захватывается по ссылке, а не по значению, из-за этого ее значение во время работы потока может отличаться, так что создаем локальную копию.
                     */
                    int currentY = y; // Локальная копия для замыкания
                    //Помещаем объект в очередь задач
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        for (int x = 0; x < width; x++)
                        {
                            double red = 0.0, green = 0.0, blue = 0.0;

                            for (int fy = 0; fy < filterSize; fy++)
                            {
                                for (int fx = 0; fx < filterSize; fx++)
                                {
                                    int imageX = x + fx - filterOffset;
                                    int imageY = currentY + fy - filterOffset;
                                    imageX = Math.Clamp(imageX, 0, width - 1);
                                    imageY = Math.Clamp(imageY, 0, height - 1);

                                    int sourceIndex = (imageY * width + imageX) * 4;
                                    blue += sourcePixels[sourceIndex] * filter[fy, fx];
                                    green += sourcePixels[sourceIndex + 1] * filter[fy, fx];
                                    red += sourcePixels[sourceIndex + 2] * filter[fy, fx];
                                }
                            }

                            red = Math.Clamp(red * factor, 0, 255);
                            green = Math.Clamp(green * factor, 0, 255);
                            blue = Math.Clamp(blue * factor, 0, 255);

                            int resultIndex = (currentY * width + x) * 4;
                            resultPixels[resultIndex] = (byte)blue;
                            resultPixels[resultIndex + 1] = (byte)green;
                            resultPixels[resultIndex + 2] = (byte)red;
                            resultPixels[resultIndex + 3] = 255; // Альфа-канал
                        }
                        //Сообщаем, что поток завершил работу
                        countdownEvent.Signal();
                    });
                }

                // Ожидаем завершения всех задач
                countdownEvent.Wait();
            }

            // Копируем результат обратно в Bitmap
            BitmapData resultData = sourceBitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            Marshal.Copy(resultPixels, 0, resultData.Scan0, resultPixels.Length);
            sourceBitmap.UnlockBits(resultData);

            return sourceBitmap;
        }


        private double GetFilterSum(double[,] filter)
        {
            double sum = 0;
            for (int i = 0; i < filter.GetLength(0); i++)
            {
                for (int j = 0; j < filter.GetLength(1); j++)
                {
                    sum += filter[i, j];
                }
            }
            return sum == 0 ? 1 : sum;
        }

        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (filteredImage == null)
            {
                MessageBox.Show("Нет изображения для сохранения.");
                return;
            }

            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Filter = "JPEG Image|*.jpg|PNG Image|*.png"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    BitmapEncoder encoder = saveFileDialog.FilterIndex == 1 ?
                        new JpegBitmapEncoder() : (BitmapEncoder)new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(filteredImage));

                    using (FileStream stream = new FileStream(saveFileDialog.FileName, FileMode.Create))
                    {
                        encoder.Save(stream);
                    }

                    MessageBox.Show("Изображение успешно сохранено.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка сохранения изображения: {ex.Message}");
                }
            }
        }

        private void FilterTypeRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (ManualOptionsPanel != null && PresetOptionsPanel != null &&
                ManualRadioButton != null && PresetRadioButton != null)
            {
                ManualOptionsPanel.Visibility = ManualRadioButton.IsChecked == true ?
                    Visibility.Visible : Visibility.Collapsed;
                PresetOptionsPanel.Visibility = PresetRadioButton.IsChecked == true ?
                    Visibility.Visible : Visibility.Collapsed;
            }
        }

        // Вспомогательные методы для конвертации между Bitmap и BitmapSource
        private Bitmap BitmapSourceToBitmap(BitmapSource bitmapSource)
        {
            Bitmap bitmap;
            using (MemoryStream outStream = new MemoryStream())
            {
                BitmapEncoder enc = new BmpBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bitmapSource));
                enc.Save(outStream);
                bitmap = new Bitmap(outStream);
            }
            return bitmap;
        }

        private BitmapSource BitmapToBitmapSource(Bitmap bitmap)
        {
            // Создаем копию Bitmap, чтобы избежать проблем с блокировкой
            using (Bitmap tempBitmap = new Bitmap(bitmap))
            using (MemoryStream memory = new MemoryStream())
            {
                tempBitmap.Save(memory, ImageFormat.Png);
                memory.Position = 0;

                BitmapImage bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memory;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad; // Загружаем сразу, чтобы не зависеть от потока
                bitmapImage.EndInit();
                bitmapImage.Freeze(); // Делаем BitmapImage потокобезопасным (если нужно использовать в WPF UI)

                return bitmapImage;
            }
        }
    }
}