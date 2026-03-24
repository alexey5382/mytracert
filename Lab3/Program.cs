using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace P2PChat
{
    class Program
    {
        private const byte TYPE_MESSAGE = 1;
        private const byte TYPE_INTRODUCE = 2;

        private static string myName = "";
        private static IPAddress myIpAddress;
        private static int myUdpPort;
        private static int myTcpPort;

        private static ConcurrentDictionary<string, NodeConnection> activeNodes = new();
        private static readonly object nodesLock = new object();
        private static readonly object consoleLock = new object();

        class NodeConnection
        {
            public string Name { get; set; }
            public string Ip { get; set; }
            public TcpClient Client { get; set; }
            public NetworkStream Stream { get; set; }
        }

        static async Task Main(string[] args)
        {
            TcpListener tcpListener = null;
            UdpClient udpServer = null;

            // Цикл инициализации (возвращает в начало при ошибке)
            while (true)
            {
                Console.Clear();
                Console.WriteLine("Введите данные для запуска через пробел: [Имя] [IP-адрес] [UDP_Порт] [TCP_Порт]");
                Console.WriteLine("Пример: MyName 127.0.0.1 8888 9001");
                Console.Write("> ");

                string input = Console.ReadLine();
                if (!TryParseInput(input))
                {
                    Console.WriteLine("Ошибка ввода. Пожалуйста, проверьте правильность форматов.");
                    Console.WriteLine("Нажмите любую клавишу для повтора...");
                    Console.ReadKey();
                    continue;
                }

                try
                {
                    IPEndPoint localUdpEp = new IPEndPoint(myIpAddress, myUdpPort);
                    udpServer = new UdpClient(localUdpEp);
                    udpServer.EnableBroadcast = true;

                    tcpListener = new TcpListener(myIpAddress, myTcpPort);
                    tcpListener.Start();

                    break;
                }
                catch (SocketException ex)
                {
                    udpServer?.Close();
                    tcpListener?.Stop();

                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[ОШИБКА] Не удалось занять указанный IP или порты.");
                    Console.WriteLine($"Детали: {ex.Message}");
                    Console.WriteLine("Возможно, этот адрес и порты уже используются другим узлом.");
                    Console.ResetColor();
                    Console.WriteLine("\nНажмите любую клавишу, чтобы попробовать снова...");
                    Console.ReadKey();
                }
            }

            Console.Clear();
            Console.WriteLine($"Запуск чата. IP: {myIpAddress}, Имя: {myName}");
            Log("Запуск узла", ConsoleColor.Green);

            _ = Task.Run(() => StartTcpListener(tcpListener));
            _ = Task.Run(() => StartUdpListener(udpServer));

            await Task.Delay(500);
            BroadcastPresence();

            Log("Введите сообщение (или 'exit' для выхода):", ConsoleColor.Gray);

            while (true)
            {
                string message = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(message)) continue;

                if (message.Trim().ToLower() == "exit")
                {
                    Shutdown();
                    break;
                }

                Log($"Я: {message}", ConsoleColor.Cyan);
                await BroadcastMessageToAllTcpAsync(TYPE_MESSAGE, message);
            }
        }
        private static bool TryParseInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) return false;

            myName = parts[0];
            if (!IPAddress.TryParse(parts[1], out myIpAddress)) return false;
            if (!int.TryParse(parts[2], out myUdpPort) || myUdpPort < 1 || myUdpPort > 65535) return false;
            if (!int.TryParse(parts[3], out myTcpPort) || myTcpPort < 1 || myTcpPort > 65535) return false;

            return true;
        }

        private static void Shutdown()
        {
            foreach (var kvp in activeNodes)
            {
                try
                {
                    kvp.Value.Client.Close();
                }
                catch { }
            }
            activeNodes.Clear();
            Log("Узел отключен", ConsoleColor.Red);
        }

        private static bool RegisterConnection(string nodeId, NodeConnection newConn, bool isIncoming)
        {
            lock (nodesLock)
            {
                if (activeNodes.TryGetValue(nodeId, out var existingConn))
                {
                    string myId = $"{myIpAddress}:{myTcpPort}";
                    bool iWin = string.Compare(myId, nodeId, StringComparison.Ordinal) > 0;
                    bool keepNew = isIncoming ? !iWin : iWin;

                    if (keepNew)
                    {
                        existingConn.Client.Close();
                        activeNodes[nodeId] = newConn;
                        return true;
                    }
                    return false;
                }
                else
                {
                    activeNodes[nodeId] = newConn;
                    return true;
                }
            }
        }

        private static void StartUdpListener(UdpClient udpServer)
        {
            try
            {
                IPEndPoint localEp = new IPEndPoint(myIpAddress, myUdpPort);
                udpServer.EnableBroadcast = true;

                while (true)
                {
                    IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = udpServer.Receive(ref remoteEp);
                    string receivedData = Encoding.UTF8.GetString(data);

                    var parts = receivedData.Split('|');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int remoteTcpPort))
                    {
                        string remoteName = parts[0];
                        string remoteIp = remoteEp.Address.ToString();
                        string nodeId = $"{remoteIp}:{remoteTcpPort}";

                        // Если пакет не от нас самих
                        if (nodeId != $"{myIpAddress}:{myTcpPort}")
                        {
                            Log($"Получен UDP пакет от {remoteName} ( {remoteIp} )", ConsoleColor.DarkGray);
                            _ = Task.Run(() => ConnectToNewNodeAsync(remoteIp, remoteTcpPort, remoteName));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка UDP: {ex.Message}", ConsoleColor.Red);
                
            }
        }

        private static void BroadcastPresence()
        {
            try
            {
                using UdpClient udpClient = new UdpClient(new IPEndPoint(myIpAddress, 0));
                udpClient.EnableBroadcast = true;
                IPEndPoint broadcastEp = new IPEndPoint(IPAddress.Broadcast, myUdpPort);
                byte[] data = Encoding.UTF8.GetBytes($"{myName}|{myTcpPort}");
                udpClient.Send(data, data.Length, broadcastEp);

                Log("Отправлен широковещательный UDP пакет", ConsoleColor.DarkGray);
            }
            catch (Exception ex)
            {
                Log($"Ошибка Broadcast: {ex.Message}", ConsoleColor.Red);
            }
        }

        private static void StartTcpListener(TcpListener listener)
        {
            try
            {
                listener.Start();

                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    _ = Task.Run(() => HandleIncomingConnectionAsync(client));
                }
            }
            catch (Exception ex)
            {
            }
        }

        private static async Task ConnectToNewNodeAsync(string ip, int port, string remoteName)
        {
            string nodeId = $"{ip}:{port}";
            try
            {
                Log($"Попытка подключиться к {ip}...", ConsoleColor.DarkGray);

                TcpClient client = new TcpClient(new IPEndPoint(myIpAddress, 0));
                await client.ConnectAsync(ip, port);
                NetworkStream stream = client.GetStream();

                var nodeConn = new NodeConnection { Client = client, Stream = stream, Name = remoteName, Ip = ip };

                if (RegisterConnection(nodeId, nodeConn, isIncoming: false))
                {
                    Log($"Установлено исходящее соединение с {remoteName} ({ip})", ConsoleColor.DarkGray);
                    Log($"Пользователь {remoteName} ({ip}) подключился", ConsoleColor.Green);

                    await SendMessageAsync(stream, TYPE_INTRODUCE, $"{myName}|{myTcpPort}");
                    _ = Task.Run(() => ReceiveMessagesLoopAsync(nodeId, nodeConn));
                }
                else
                {
                    client.Close();
                }
            }
            catch {}
        }

        private static async Task HandleIncomingConnectionAsync(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            try
            {
                var (type, payload) = await ReadMessageAsync(stream);
                if (type == TYPE_INTRODUCE)
                {
                    var parts = payload.Split('|');
                    if (parts.Length == 2)
                    {
                        string remoteName = parts[0];
                        string remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                        int remoteTcpPort = int.Parse(parts[1]);
                        string nodeId = $"{remoteIp}:{remoteTcpPort}";

                        var nodeConn = new NodeConnection { Client = client, Stream = stream, Name = remoteName, Ip = remoteIp };

                        if (RegisterConnection(nodeId, nodeConn, isIncoming: true))
                        {
                            Log($"Входящее TCP соединение от {remoteName} ({remoteIp})", ConsoleColor.DarkGray);
                            Log($"Пользователь {remoteName} ({remoteIp}) подключился", ConsoleColor.Green);

                            _ = Task.Run(() => ReceiveMessagesLoopAsync(nodeId, nodeConn));
                        }
                        else
                        {
                            client.Close();
                        }
                    }
                }
            }
            catch { client.Close(); }
        }

        private static async Task ReceiveMessagesLoopAsync(string nodeId, NodeConnection nodeConn)
        {
            try
            {
                while (true)
                {
                    var (type, payload) = await ReadMessageAsync(nodeConn.Stream);
                    if (type == TYPE_MESSAGE)
                    {
                        Log($"{nodeConn.Name} ({nodeConn.Ip}): {payload}", ConsoleColor.White);
                    }
                }
            }
            catch
            {
                HandleDisconnect(nodeId, nodeConn);
            }
        }

        private static async Task<(byte type, string payload)> ReadMessageAsync(NetworkStream stream)
        {
            byte[] header = await ReadExactlyAsync(stream, 5);
            byte type = header[0];
            int length = BitConverter.ToInt32(header, 1);

            byte[] payloadBytes = await ReadExactlyAsync(stream, length);
            string payload = Encoding.UTF8.GetString(payloadBytes);

            return (type, payload);
        }

        private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, totalRead, count - totalRead);
                if (read == 0) throw new EndOfStreamException("Соединение разорвано");
                totalRead += read;
            }
            return buffer;
        }

        private static async Task SendMessageAsync(NetworkStream stream, byte type, string payload)
        {
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
            byte[] lengthBytes = BitConverter.GetBytes(payloadBytes.Length);

            byte[] message = new byte[1 + 4 + payloadBytes.Length];
            message[0] = type;
            Buffer.BlockCopy(lengthBytes, 0, message, 1, 4);
            Buffer.BlockCopy(payloadBytes, 0, message, 5, payloadBytes.Length);

            await stream.WriteAsync(message, 0, message.Length);
        }

        private static async Task BroadcastMessageToAllTcpAsync(byte type, string payload)
        {
            foreach (var kvp in activeNodes)
            {
                try
                {
                    await SendMessageAsync(kvp.Value.Stream, type, payload);
                }
                catch {}
            }
        }

        private static void HandleDisconnect(string nodeId, NodeConnection nodeConn)
        {
            lock (nodesLock)
            {
                if (activeNodes.TryGetValue(nodeId, out var currentConn) && currentConn == nodeConn)
                {
                    activeNodes.TryRemove(nodeId, out _);
                    Log($"Пользователь {nodeConn.Name} ({nodeConn.Ip}) отключился", ConsoleColor.Red);
                }
            }
            nodeConn.Client.Close();
        }

        private static void Log(string message, ConsoleColor color = ConsoleColor.Gray)
        {
            lock (consoleLock)
            {
                string time = DateTime.Now.ToString("HH:mm:ss");

                Console.ForegroundColor = color;
                Console.WriteLine($"[{time}] {message}");
                Console.ResetColor();
            }
        }
    }
}
