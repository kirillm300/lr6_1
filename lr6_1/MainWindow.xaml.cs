// Полный исправленный файл MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace LegalConsultation
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<string> queueList = new ObservableCollection<string>();
        private readonly ObservableCollection<string> lawyersStatus = new ObservableCollection<string>();
        private readonly List<Lawyer> lawyers = new List<Lawyer>();
        private readonly Queue<Client> clientQueue = new Queue<Client>();
        private readonly object queueLock = new object();
        private readonly object fileLock = new object();
        private readonly SemaphoreSlim highCategorySemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim regularLawyerSemaphore = new SemaphoreSlim(5, 5);
        private readonly Random random = new Random();
        private readonly string logFilePath = "consultation_log.txt";
        private CancellationTokenSource cts = new CancellationTokenSource();
        private int clientCounter = 0;
        private List<double> waitingTimes = new List<double>();
        private bool isRunning = false;

        public MainWindow()
        {
            InitializeComponent();
            QueueListBox.ItemsSource = queueList;
            LawyersListBox.ItemsSource = lawyersStatus;

            // Initialize lawyers
            lawyers.Add(new Lawyer(0, true)); // High category
            for (int i = 1; i <= 5; i++)
                lawyers.Add(new Lawyer(i, false));
            UpdateLawyersStatus();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isRunning)
            {
                isRunning = true;
                cts = new CancellationTokenSource();
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                Task.Run(() => ClientArrivalSimulation(cts.Token));
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (isRunning)
            {
                cts.Cancel();
                isRunning = false;
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                UpdateAverageWaitingTime();
            }
        }

        private async Task ClientArrivalSimulation(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    double interArrivalTime = -Math.Log(1 - random.NextDouble()) * 3 * 60 * 1000;
                    await Task.Delay((int)interArrivalTime, token);

                    Client client = new Client(clientCounter++, clientCounter % 5 == 0);
                    lock (queueLock)
                    {
                        clientQueue.Enqueue(client);
                        Dispatcher.Invoke(() => queueList.Add($"Клиент {client.Id} (Предпочитает высшую категорию: {client.PrefersHighCategory})"));
                        LogToFile($"Client {client.Id} arrived at {client.EnqueueTime}");
                    }

                    Task.Run(() => ProcessClient(client, token));
                }
            }
            catch (OperationCanceledException)
            {
                LogToFile("Simulation stopped.");
            }
            catch (Exception ex)
            {
                LogToFile($"Error in client arrival: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ProcessClient(Client client, CancellationToken token)
        {
            Lawyer assignedLawyer = null;
            bool semaphoreReleased = false;

            try
            {
                if (client.PrefersHighCategory)
                {
                    if (highCategorySemaphore.CurrentCount == 0)
                    {
                        await highCategorySemaphore.WaitAsync(token);
                    }
                    else
                    {
                        await highCategorySemaphore.WaitAsync(token);
                    }
                    assignedLawyer = lawyers[0];
                }
                else
                {
                    if (highCategorySemaphore.CurrentCount > 0)
                    {
                        await highCategorySemaphore.WaitAsync(token);
                        assignedLawyer = lawyers[0];
                    }
                    else
                    {
                        await regularLawyerSemaphore.WaitAsync(token);
                        List<Lawyer> availableLawyers;
                        lock (queueLock)
                        {
                            availableLawyers = lawyers.Where(l => !l.IsHighCategory && !l.IsBusy).ToList();
                        }
                        if (availableLawyers.Any())
                        {
                            assignedLawyer = availableLawyers[random.Next(availableLawyers.Count)];
                        }
                    }
                }

                if (assignedLawyer != null)
                {
                    assignedLawyer.IsBusy = true;
                    UpdateLawyersStatus();
                    lock (queueLock)
                    {
                        clientQueue.Dequeue();
                        Dispatcher.Invoke(() => queueList.Remove($"Клиент {client.Id} (Предпочитает высшую категорию: {client.PrefersHighCategory})"));
                    }

                    double waitingTime = (DateTime.Now - client.EnqueueTime).TotalSeconds;
                    lock (waitingTimes)
                    {
                        waitingTimes.Add(waitingTime);
                    }

                    double serviceTime = -Math.Log(1 - random.NextDouble()) * 10 * 60 * 1000;
                    LogToFile($"Client {client.Id} assigned to Lawyer {assignedLawyer.Id} at {DateTime.Now}, waiting time: {waitingTime:F2} sec");
                    await Task.Delay((int)serviceTime, token);

                    assignedLawyer.IsBusy = false;
                    if (assignedLawyer.IsHighCategory)
                    {
                        highCategorySemaphore.Release();
                    }
                    else
                    {
                        regularLawyerSemaphore.Release();
                    }
                    semaphoreReleased = true;
                    UpdateLawyersStatus();
                    LogToFile($"Client {client.Id} finished with Lawyer {assignedLawyer.Id} at {DateTime.Now}");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogToFile($"Error processing client {client.Id}: {ex.Message}");
                MessageBox.Show($"Error processing client {client.Id}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (assignedLawyer != null && !semaphoreReleased)
                {
                    assignedLawyer.IsBusy = false;
                    if (assignedLawyer.IsHighCategory)
                        highCategorySemaphore.Release();
                    else
                        regularLawyerSemaphore.Release();
                    UpdateLawyersStatus();
                }
            }
        }

        private void UpdateLawyersStatus()
        {
            Dispatcher.Invoke(() =>
            {
                lawyersStatus.Clear();
                foreach (var lawyer in lawyers)
                {
                    lawyersStatus.Add($"Юрист {lawyer.Id} {(lawyer.IsHighCategory ? "(Высшая категория)" : "")}: {(lawyer.IsBusy ? "Занят" : "Свободен")}");
                }
            });
        }

        private void UpdateAverageWaitingTime()
        {
            double avgWaitingTime = 0;
            lock (waitingTimes)
            {
                if (waitingTimes.Any())
                    avgWaitingTime = waitingTimes.Average();
            }
            Dispatcher.Invoke(() =>
            {
                AvgWaitingTimeText.Text = $"Среднее время ожидания: {avgWaitingTime:F2} секунд";
            });
        }

        private void LogToFile(string message)
        {
            lock (fileLock)
            {
                try
                {
                    using (StreamWriter writer = new StreamWriter(logFilePath, true))
                    {
                        writer.WriteLine($"{DateTime.Now}: {message}");
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Error writing to log: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            cts.Cancel();
            base.OnClosing(e);
        }
    }

    public class Client
    {
        public int Id { get; }
        public bool PrefersHighCategory { get; }
        public DateTime EnqueueTime { get; }

        public Client(int id, bool prefersHighCategory)
        {
            Id = id;
            PrefersHighCategory = prefersHighCategory;
            EnqueueTime = DateTime.Now;
        }
    }

    public class Lawyer
    {
        public int Id { get; }
        public bool IsHighCategory { get; }
        public bool IsBusy { get; set; }

        public Lawyer(int id, bool isHighCategory)
        {
            Id = id;
            IsHighCategory = isHighCategory;
            IsBusy = false;
        }
    }
}
