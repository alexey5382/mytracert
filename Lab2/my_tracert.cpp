#include <iostream>
#include <string>
#include <vector>
#include <chrono>
#include <winsock2.h>
#include <ws2tcpip.h>

#pragma comment(lib, "ws2_32.lib")

using namespace std;

struct ICMPHeader {
    uint8_t type;
    uint8_t code;
    uint16_t checksum;
    uint16_t id;
    uint16_t sequence;
};

uint16_t calculateChecksum(uint16_t* buffer, int length) {
    uint32_t sum = 0;
    while (length > 1) {
        sum += *buffer++;
        length -= 2;
    }
    if (length == 1) sum += *(uint8_t*)buffer;
    sum = (sum >> 16) + (sum & 0xFFFF);
    sum += (sum >> 16);
    return (uint16_t)(~sum);
}

int main(int argc, char* argv[]) {
    bool resolveNames = false;
    int maxHops = 30;
    string targetHost = "";

    for (int i = 1; i < argc; ++i) {
        string arg = argv[i];
        if (arg == "-r") {
            resolveNames = true;
        }
        else if (arg == "-h") {
            if (i + 1 < argc) {
                maxHops = stoi(argv[++i]);
            }
            else {
                cerr << "Ошибка: не указано значение для параметра -h." << endl;
                return 1;
            }
        }
        else if (arg[0] != '-') {
            targetHost = arg;
        }
        else {
            cerr << "Неизвестный параметр: " << arg << endl;
            return 1;
        }
    }

    if (targetHost.empty()) {
        cerr << "Использование: " << argv[0] << " [-r] [-h <макс_прыжков>] <IP или Домен>" << endl;
        return 1;
    }

    WSADATA wsaData;
    WSAStartup(MAKEWORD(2, 2), &wsaData);

    addrinfo hints = { 0 }, * res = nullptr;
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_RAW;
    hints.ai_protocol = IPPROTO_ICMP;

    if (getaddrinfo(targetHost.c_str(), nullptr, &hints, &res) != 0) {
        cerr << "Ошибка резолва цели." << endl;
        WSACleanup();
        return 1;
    }

    sockaddr_in destAddr = *(sockaddr_in*)res->ai_addr;
    char destIpStr[INET_ADDRSTRLEN];
    inet_ntop(AF_INET, &(destAddr.sin_addr), destIpStr, INET_ADDRSTRLEN);
    freeaddrinfo(res);
    cout << "new\n";
    cout << "Трассировка к " << targetHost << " [" << destIpStr << "]" << endl;
    cout << "С максимальным числом прыжков: " << maxHops << "\n" << endl;

    SOCKET rawSocket = socket(AF_INET, SOCK_RAW, IPPROTO_ICMP);
    DWORD timeout = 2000;
    setsockopt(rawSocket, SOL_SOCKET, SO_RCVTIMEO, (const char*)&timeout, sizeof(timeout));

    bool targetReached = false;
    uint16_t seq_no = 0;

    for (int ttl = 1; ttl <= maxHops && !targetReached; ++ttl) {
        setsockopt(rawSocket, IPPROTO_IP, IP_TTL, (const char*)&ttl, sizeof(ttl));
        cout << ttl << "\t";

        sockaddr_in lastReplyAddr;
        bool hopResponded = false;

        for (int i = 0; i < 3; ++i) {
            ICMPHeader icmpReq = { 8, 0, 0, htons((uint16_t)GetCurrentProcessId()), htons(++seq_no) };
            icmpReq.checksum = calculateChecksum((uint16_t*)&icmpReq, sizeof(icmpReq));

            auto start = chrono::steady_clock::now();
            sendto(rawSocket, (const char*)&icmpReq, sizeof(icmpReq), 0, (sockaddr*)&destAddr, sizeof(destAddr));

            char recvBuffer[1024];
            int addrLen = sizeof(lastReplyAddr);
            bool packetFound = false;

            while (true) {
                int bytes = recvfrom(rawSocket, recvBuffer, sizeof(recvBuffer), 0, (sockaddr*)&lastReplyAddr, &addrLen);

                if (bytes == SOCKET_ERROR) {
                    if (WSAGetLastError() == WSAETIMEDOUT) break;
                    break;
                }

                auto end = chrono::steady_clock::now();
                int ipHeaderLen = (recvBuffer[0] & 0x0F) * 4;
                ICMPHeader* icmpReply = (ICMPHeader*)(recvBuffer + ipHeaderLen);

                if (icmpReply->type == 0 && ntohs(icmpReply->id) == (uint16_t)GetCurrentProcessId()) {
                    auto ms = chrono::duration_cast<chrono::milliseconds>(end - start).count();
                    cout << ms << " мс\t";
                    packetFound = true;
                    hopResponded = true;
                    targetReached = true;
                    break;
                }

                if (icmpReply->type == 11) {
                    ICMPHeader* innerIcmp = (ICMPHeader*)(recvBuffer + ipHeaderLen + 8 + 20);
                    if (ntohs(innerIcmp->id) == (uint16_t)GetCurrentProcessId()) {
                        auto ms = chrono::duration_cast<chrono::milliseconds>(end - start).count();
                        cout << ms << " мс\t";
                        packetFound = true;
                        hopResponded = true;
                        break;
                    }
                }
            }

            if (!packetFound) cout << "*\t";
        }

        if (hopResponded) {
            char ipStr[INET_ADDRSTRLEN];
            inet_ntop(AF_INET, &(lastReplyAddr.sin_addr), ipStr, INET_ADDRSTRLEN);

            if (resolveNames) {
                char hostName[NI_MAXHOST];
                if (getnameinfo((sockaddr*)&lastReplyAddr, sizeof(lastReplyAddr), hostName, NI_MAXHOST, nullptr, 0, 0) == 0) {
                    if (string(hostName) == string(ipStr)) {
                        cout << ipStr;
                    }
                    else {
                        cout << hostName << " [" << ipStr << "]";
                    }
                }
                else {
                    cout << ipStr;
                }
            }
            else {
                cout << ipStr;
            }
        }
        else {
            cout << "Превышен интервал ожидания.";
        }
        cout << endl;
    }

    cout << "\nТрассировка завершена.\n" << endl;

    closesocket(rawSocket);
    WSACleanup();
    return 0;
}
