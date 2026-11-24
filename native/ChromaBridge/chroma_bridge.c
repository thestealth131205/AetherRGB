/*
 * AetherRGB Chroma-Bridge — Ersatz für RzChromaSDK64.dll / RzChromaSDK.dll.
 *
 * Spiele mit Razer-Chroma-Unterstützung laden diese DLL und rufen Init, CreateKeyboardEffect usw.
 * auf. Die Bridge macht damit absichtlich nichts selbst: Sie verpackt jeden Effekt unverändert
 * (das rohe Parameter-Struct aus dem Spiel) in ein UDP-Paket an 127.0.0.1:34535 und schickt es an
 * AetherRGB. Dort wird es ausgewertet und auf die eigenen LEDs gemappt. Läuft AetherRGB nicht,
 * verpuffen die Pakete — das Spiel merkt davon nichts, jede Funktion meldet Erfolg.
 *
 * Bewusst so klein wie möglich, weil der Code im Prozess des Spiels läuft: keine Threads, kein
 * Empfang, keine Dateizugriffe, nur nicht-blockierendes sendto() auf die Loopback-Adresse.
 *
 * Paketformat (Little Endian), siehe ChromaBridgeProtocol.cs:
 *   "AECB" | u8 Version=1 | u8 Art | u8 Gerät | u8 0 | u32 PID | i32 Effekttyp | u32 Länge | Nutzdaten
 */
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <objbase.h>
#include <string.h>

#define BRIDGE_PORT 34535
#define MAX_EFFECTS 512
#define MAX_PAYLOAD 2048

/* Erkennungsmerkmal: AetherRGB sucht diese Zeichenkette in der Datei, bevor es eine vorhandene
 * RzChromaSDK64.dll ersetzt oder löscht — eine echte Razer-DLL wird nie angefasst. */
__declspec(dllexport) const char AetherChromaBridgeMarker[] = "AetherRGB-ChromaBridge/1";

typedef LONG RZRESULT;
typedef GUID RZEFFECTID;
typedef GUID RZDEVICEID;

#define RZRESULT_SUCCESS            0
#define RZRESULT_INVALID_PARAMETER  87
#define RZRESULT_NOT_FOUND          1168

enum { KIND_INIT = 1, KIND_UNINIT = 2, KIND_EFFECT = 3 };
enum { DEV_GENERIC = 0, DEV_KEYBOARD = 1, DEV_MOUSE = 2, DEV_HEADSET = 3, DEV_MOUSEPAD = 4, DEV_KEYPAD = 5, DEV_CHROMALINK = 6 };

#pragma pack(push, 1)
typedef struct {
    char magic[4];
    unsigned char version;
    unsigned char kind;
    unsigned char device;
    unsigned char reserved;
    unsigned int pid;
    int effectType;
    unsigned int length;
} PacketHeader;
#pragma pack(pop)

/* Muss zu HeaderSize in ChromaBridgeListener.cs passen. */
typedef char HeaderSizeCheck[(sizeof(PacketHeader) == 20) ? 1 : -1];

typedef struct {
    int used;
    GUID id;
    unsigned char device;
    int effectType;
    unsigned int length;
    unsigned char data[MAX_PAYLOAD];
} StoredEffect;

static CRITICAL_SECTION g_lock;
static SOCKET g_socket = INVALID_SOCKET;
static struct sockaddr_in g_target;
static int g_wsaStarted = 0;
static StoredEffect *g_effects = NULL; /* MAX_EFFECTS Einträge, erst bei Init angelegt */

/* Größe des Parameter-Structs je Gerät und Effekttyp, wie in RzChromaSDKTypes.h. -1 = unbekannter
 * Effekttyp (wird ignoriert, aber mit Erfolg beantwortet). 0 = Effekt ohne Parameter. */
static int ParamSize(unsigned char device, int type)
{
    switch (device) {
    case DEV_KEYBOARD:
        switch (type) {
        case 0: return 0;                       /* CHROMA_NONE */
        case 1: return 12;                      /* CHROMA_BREATHING (veraltet) */
        case 2: return 6 * 22 * 4;              /* CHROMA_CUSTOM */
        case 3: return 8;                       /* CHROMA_REACTIVE */
        case 4: return 4;                       /* CHROMA_STATIC */
        case 5: return 0;                       /* CHROMA_SPECTRUMCYCLING */
        case 6: return 4;                       /* CHROMA_WAVE */
        case 8: return 2 * 6 * 22 * 4;          /* CHROMA_CUSTOM_KEY */
        case 9: return (8 * 24 + 6 * 22) * 4;   /* CHROMA_CUSTOM2 */
        }
        return -1;
    case DEV_MOUSE:
        switch (type) {
        case 0: return 0;
        case 1: return 8;                       /* CHROMA_BLINKING */
        case 2: return 16;                      /* CHROMA_BREATHING */
        case 3: return 30 * 4;                  /* CHROMA_CUSTOM */
        case 4: return 12;                      /* CHROMA_REACTIVE */
        case 5: return 4;                       /* CHROMA_SPECTRUMCYCLING */
        case 6: return 8;                       /* CHROMA_STATIC */
        case 7: return 4;                       /* CHROMA_WAVE */
        case 8: return 9 * 7 * 4;               /* CHROMA_CUSTOM2 */
        }
        return -1;
    case DEV_HEADSET:
        switch (type) {
        case 0: return 0;
        case 1: return 4;                       /* CHROMA_STATIC */
        case 2: return 4;                       /* CHROMA_BREATHING */
        case 3: return 0;                       /* CHROMA_SPECTRUMCYCLING */
        case 4: return 5 * 4;                   /* CHROMA_CUSTOM */
        }
        return -1;
    case DEV_MOUSEPAD:
        switch (type) {
        case 0: return 0;
        case 1: return 12;                      /* CHROMA_BREATHING */
        case 2: return 15 * 4;                  /* CHROMA_CUSTOM */
        case 3: return 0;                       /* CHROMA_SPECTRUMCYCLING */
        case 4: return 4;                       /* CHROMA_STATIC */
        case 5: return 4;                       /* CHROMA_WAVE */
        case 6: return 20 * 4;                  /* CHROMA_CUSTOM2 */
        }
        return -1;
    case DEV_KEYPAD:
        switch (type) {
        case 0: return 0;
        case 1: return 12;                      /* CHROMA_BREATHING */
        case 2: return 4 * 5 * 4;               /* CHROMA_CUSTOM */
        case 3: return 8;                       /* CHROMA_REACTIVE */
        case 4: return 0;                       /* CHROMA_SPECTRUMCYCLING */
        case 5: return 4;                       /* CHROMA_STATIC */
        case 6: return 4;                       /* CHROMA_WAVE */
        }
        return -1;
    case DEV_CHROMALINK:
        switch (type) {
        case 0: return 0;
        case 1: return 5 * 4;                   /* CHROMA_CUSTOM */
        case 2: return 4;                       /* CHROMA_STATIC */
        }
        return -1;
    }
    return -1;
}

static void Send(unsigned char kind, unsigned char device, int effectType, const void *data, unsigned int length)
{
    unsigned char buffer[sizeof(PacketHeader) + MAX_PAYLOAD];
    PacketHeader *h = (PacketHeader *)buffer;
    if (g_socket == INVALID_SOCKET || length > MAX_PAYLOAD) return;
    memcpy(h->magic, "AECB", 4);
    h->version = 1;
    h->kind = kind;
    h->device = device;
    h->reserved = 0;
    h->pid = GetCurrentProcessId();
    h->effectType = effectType;
    h->length = length;
    if (length > 0) memcpy(buffer + sizeof(PacketHeader), data, length);
    /* Nicht blockierend — ein Fehler (AetherRGB läuft nicht) wird bewusst ignoriert. */
    sendto(g_socket, (const char *)buffer, (int)(sizeof(PacketHeader) + length), 0,
           (const struct sockaddr *)&g_target, sizeof(g_target));
}

static RZRESULT OpenBridge(const wchar_t *title)
{
    wchar_t name[256];
    unsigned int length;
    u_long nonBlocking = 1;
    WSADATA wsa;

    EnterCriticalSection(&g_lock);
    if (g_socket == INVALID_SOCKET) {
        if (!g_wsaStarted && WSAStartup(MAKEWORD(2, 2), &wsa) == 0) g_wsaStarted = 1;
        g_socket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (g_socket != INVALID_SOCKET) ioctlsocket(g_socket, FIONBIO, &nonBlocking);
        memset(&g_target, 0, sizeof(g_target));
        g_target.sin_family = AF_INET;
        g_target.sin_port = htons(BRIDGE_PORT);
        g_target.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        if (g_effects == NULL)
            g_effects = (StoredEffect *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(StoredEffect) * MAX_EFFECTS);
    }

    /* Name für das Log in AetherRGB: Titel aus InitSDK, sonst Name der EXE. */
    name[0] = 0;
    if (title != NULL && title[0] != 0) {
        lstrcpynW(name, title, 256);
    } else {
        wchar_t path[MAX_PATH];
        DWORD n = GetModuleFileNameW(NULL, path, MAX_PATH);
        if (n > 0 && n < MAX_PATH) {
            const wchar_t *base = path;
            const wchar_t *p;
            for (p = path; *p; p++) if (*p == L'\\' || *p == L'/') base = p + 1;
            lstrcpynW(name, base, 256);
        }
    }
    length = (unsigned int)(lstrlenW(name) * sizeof(wchar_t));
    Send(KIND_INIT, DEV_GENERIC, 0, name, length);
    LeaveCriticalSection(&g_lock);
    return RZRESULT_SUCCESS;
}

static RZRESULT CreateDeviceEffect(unsigned char device, int type, const void *param, RZEFFECTID *effectId)
{
    int size = ParamSize(device, type);
    RZRESULT result = RZRESULT_SUCCESS;
    int i;

    if (size < 0) {
        /* Unbekannter Effekttyp: nicht weiterleiten, aber eine gültige ID liefern, damit das Spiel
         * nicht abbricht. */
        if (effectId != NULL) CoCreateGuid(effectId);
        return RZRESULT_SUCCESS;
    }
    if (size > 0 && param == NULL) return RZRESULT_INVALID_PARAMETER;

    EnterCriticalSection(&g_lock);
    if (effectId == NULL) {
        /* Sofort anzeigen */
        Send(KIND_EFFECT, device, type, param, (unsigned int)size);
    } else if (g_effects != NULL) {
        /* Speichern, angezeigt wird erst mit SetEffect */
        StoredEffect *slot = NULL;
        for (i = 0; i < MAX_EFFECTS; i++) if (!g_effects[i].used) { slot = &g_effects[i]; break; }
        if (slot == NULL) slot = &g_effects[GetTickCount() % MAX_EFFECTS]; /* voll: Spiel löscht nichts — ältesten ungefähr überschreiben */
        CoCreateGuid(&slot->id);
        slot->used = 1;
        slot->device = device;
        slot->effectType = type;
        slot->length = (unsigned int)size;
        if (size > 0) memcpy(slot->data, param, (size_t)size);
        *effectId = slot->id;
    } else {
        CoCreateGuid(effectId);
        result = RZRESULT_SUCCESS;
    }
    LeaveCriticalSection(&g_lock);
    return result;
}

/* ===================== Exporte (Signaturen wie RzChromaSDK) ===================== */

__declspec(dllexport) RZRESULT Init(void) { return OpenBridge(NULL); }

/* APPINFOTYPE beginnt mit TCHAR Title[256] (Unicode) — mehr wird nicht gelesen. */
__declspec(dllexport) RZRESULT InitSDK(const void *appInfo) { return OpenBridge((const wchar_t *)appInfo); }

__declspec(dllexport) RZRESULT UnInit(void)
{
    EnterCriticalSection(&g_lock);
    Send(KIND_UNINIT, DEV_GENERIC, 0, NULL, 0);
    if (g_socket != INVALID_SOCKET) { closesocket(g_socket); g_socket = INVALID_SOCKET; }
    if (g_effects != NULL) memset(g_effects, 0, sizeof(StoredEffect) * MAX_EFFECTS);
    LeaveCriticalSection(&g_lock);
    return RZRESULT_SUCCESS;
}

__declspec(dllexport) RZRESULT CreateKeyboardEffect(int type, const void *param, RZEFFECTID *id) { return CreateDeviceEffect(DEV_KEYBOARD, type, param, id); }
__declspec(dllexport) RZRESULT CreateMouseEffect(int type, const void *param, RZEFFECTID *id) { return CreateDeviceEffect(DEV_MOUSE, type, param, id); }
__declspec(dllexport) RZRESULT CreateHeadsetEffect(int type, const void *param, RZEFFECTID *id) { return CreateDeviceEffect(DEV_HEADSET, type, param, id); }
__declspec(dllexport) RZRESULT CreateMousepadEffect(int type, const void *param, RZEFFECTID *id) { return CreateDeviceEffect(DEV_MOUSEPAD, type, param, id); }
__declspec(dllexport) RZRESULT CreateKeypadEffect(int type, const void *param, RZEFFECTID *id) { return CreateDeviceEffect(DEV_KEYPAD, type, param, id); }
__declspec(dllexport) RZRESULT CreateChromaLinkEffect(int type, const void *param, RZEFFECTID *id) { return CreateDeviceEffect(DEV_CHROMALINK, type, param, id); }

/* Allgemeiner Effekt für ein bestimmtes Gerät (GUID). Ausgewertet wird nur CHROMA_STATIC (6):
 * struct { size_t Size; DWORD Param; COLORREF Color; } — als Farbe für alle Geräte. */
__declspec(dllexport) RZRESULT CreateEffect(RZDEVICEID deviceId, int type, const void *param, RZEFFECTID *effectId)
{
    typedef struct { size_t Size; DWORD Param; COLORREF Color; } GenericStatic;
    (void)deviceId;
    if (type == 6 && param != NULL) {
        COLORREF color = ((const GenericStatic *)param)->Color;
        RZRESULT r = CreateDeviceEffect(DEV_CHROMALINK, 2, &color, effectId);
        return r;
    }
    if (effectId != NULL) CoCreateGuid(effectId);
    return RZRESULT_SUCCESS;
}

__declspec(dllexport) RZRESULT SetEffect(RZEFFECTID effectId)
{
    int i;
    RZRESULT result = RZRESULT_NOT_FOUND;
    EnterCriticalSection(&g_lock);
    if (g_effects != NULL) {
        for (i = 0; i < MAX_EFFECTS; i++) {
            if (g_effects[i].used && IsEqualGUID(&g_effects[i].id, &effectId)) {
                Send(KIND_EFFECT, g_effects[i].device, g_effects[i].effectType, g_effects[i].data, g_effects[i].length);
                result = RZRESULT_SUCCESS;
                break;
            }
        }
    }
    LeaveCriticalSection(&g_lock);
    /* IDs unbekannter Effekttypen (siehe CreateDeviceEffect) sind nicht gespeichert — trotzdem Erfolg. */
    return result == RZRESULT_NOT_FOUND ? RZRESULT_SUCCESS : result;
}

__declspec(dllexport) RZRESULT DeleteEffect(RZEFFECTID effectId)
{
    int i;
    EnterCriticalSection(&g_lock);
    if (g_effects != NULL)
        for (i = 0; i < MAX_EFFECTS; i++)
            if (g_effects[i].used && IsEqualGUID(&g_effects[i].id, &effectId)) { g_effects[i].used = 0; break; }
    LeaveCriticalSection(&g_lock);
    return RZRESULT_SUCCESS;
}

/* DEVICE_INFO_TYPE { DEVICE_TYPE DeviceType; DWORD Connected; } — jedes Gerät gilt als angeschlossen,
 * damit Spiele auch Tastatur- und Maus-Effekte schicken. */
__declspec(dllexport) RZRESULT QueryDevice(RZDEVICEID deviceId, void *info)
{
    (void)deviceId;
    if (info == NULL) return RZRESULT_INVALID_PARAMETER;
    ((int *)info)[0] = 1;
    ((DWORD *)info)[1] = 1;
    return RZRESULT_SUCCESS;
}

__declspec(dllexport) RZRESULT RegisterEventNotification(HWND hWnd) { (void)hWnd; return RZRESULT_SUCCESS; }
__declspec(dllexport) RZRESULT UnregisterEventNotification(void) { return RZRESULT_SUCCESS; }

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
        InitializeCriticalSection(&g_lock);
    } else if (reason == DLL_PROCESS_DETACH && reserved == NULL) {
        /* Nur bei FreeLibrary aufräumen; beim Prozessende räumt Windows selbst auf. Kein Winsock-
         * Aufruf hier (in DllMain nicht erlaubt) — das Prozessende erkennt AetherRGB an der PID. */
        DeleteCriticalSection(&g_lock);
    }
    return TRUE;
}
