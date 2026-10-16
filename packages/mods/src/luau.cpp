// Привязка Luau для модов: одна машина на экземпляр wasm, см. luau.ts. Строки из JS приходят через общий буфер
// (km_scratch): JS кладёт туда байты UTF-8 и зовёт функцию с их длиной. Значения кладутся на стек потока cur — это
// поток мода, а во время вызова функции хоста из скрипта — поток, который её позвал.
//
// Песочница: библиотеки и таблица kharos после km_seal только для чтения (luaL_sandbox), свои глобальные у мода — в
// отдельном потоке (luaL_sandboxthread). Нет os, io, require, load — ни файлов, ни часов, ни чужого кода. Время
// ограничивает срок (deadline): прерывание Luau смотрит на часы хоста раз в 256 точек, а после срока ошибкой
// кончается каждая точка — ошибку срока скрипт не перехватит pcall-ом. Память ограничивает распределитель (limit).

#include "lua.h"
#include "lualib.h"
#include "luacode.h"

#include <emscripten.h>
#include <stdlib.h>
#include <string.h>

#define KM_IMPORT(name) __attribute__((import_module("kharos"), import_name(#name)))
#define KM_EXPORT extern "C" EMSCRIPTEN_KEEPALIVE

extern "C"
{
    // Часы хоста в мс.
    KM_IMPORT(now) double km_host_now();
    // Функция хоста номер fn с argc аргументами на стеке cur. Возвращает число результатов, положенных на стек, или
    // −(длина + 1) сообщения об ошибке, положенного в km_scratch.
    KM_IMPORT(call) int km_host_call(int fn, int argc);
    // Строка от print.
    KM_IMPORT(log) void km_host_log(const char* text, size_t length);
}

// Типы значений для JS. Номера lua_Type меняются от версии к версии Luau, эти — нет.
enum
{
    KM_NIL = 0,
    KM_BOOLEAN = 1,
    KM_NUMBER = 2,
    KM_STRING = 3,
    KM_TABLE = 4,
    KM_OTHER = 5,
};

// Чем кончился запуск: km_load и km_emit.
enum
{
    KM_OK = 0,
    KM_ERROR = 1,
    KM_TIMEOUT = 2,
    KM_MEMORY = 3,
};

static const char* HANDLERS = "kharos.handlers";
static const char* SYSTEMS = "kharos.systems";
static const char* BUFFERS = "kharos.buffers";

static lua_State* L;
static lua_State* T;
static lua_State* cur;

static size_t used;
static size_t limit;

static double deadline;
static unsigned ticks;
static bool expired;

static char* scratch;
static size_t scratchSize;
static char* failure;
static size_t failureLength;
static size_t length;

static void* allocate(void*, void* ptr, size_t osize, size_t nsize)
{
    if (nsize == 0)
    {
        free(ptr);
        used -= osize;
        return nullptr;
    }
    if (nsize > osize && used - osize + nsize > limit)
        return nullptr;
    void* block = realloc(ptr, nsize);
    if (block)
        used = used - osize + nsize;
    return block;
}

static void interrupt(lua_State* state, int gc)
{
    // gc ≥ 0 — шаг сборщика мусора: там ошибку бросать нельзя.
    if (gc >= 0 || deadline == 0)
        return;
    if (!expired)
    {
        if (++ticks & 255)
            return;
        if (km_host_now() < deadline)
            return;
        expired = true;
    }
    luaL_error(state, "time budget exceeded");
}

static void keep(const char* text, size_t size)
{
    char* copy = (char*)realloc(failure, size + 1);
    if (!copy)
        return;
    memcpy(copy, text, size);
    copy[size] = 0;
    failure = copy;
    failureLength = size;
}

// Снимает со стека значение ошибки и запоминает его текстом.
static void keepError(lua_State* state)
{
    size_t size = 0;
    const char* text = lua_type(state, -1) == LUA_TSTRING ? lua_tolstring(state, -1, &size) : nullptr;
    if (text)
        keep(text, size);
    else
    {
        const char* name = luaL_typename(state, -1);
        static char line[64];
        size_t n = 0;
        for (const char* p = "error object is a "; *p; p++)
            line[n++] = *p;
        for (const char* p = name; *p && n < sizeof(line) - 1; p++)
            line[n++] = *p;
        keep(line, n);
    }
    lua_pop(state, 1);
}

static void begin(double budget)
{
    deadline = budget > 0 ? km_host_now() + budget : 0;
    ticks = 0;
    expired = false;
}

static int end(int status, lua_State* state)
{
    bool late = expired;
    if (status == LUA_OK)
        return KM_OK;
    keepError(state);
    if (late)
        return KM_TIMEOUT;
    if (status != LUA_ERRMEM)
        return KM_ERROR;
    // Аварийной сборки у Luau нет: брошенное упавшим скриптом держит память, пока сборщик до него не дойдёт, — и
    // следующий запуск упёрся бы в предел сразу.
    lua_gc(L, LUA_GCCOLLECT, 0);
    return KM_MEMORY;
}

static int print(lua_State* state)
{
    int count = lua_gettop(state);
    for (int i = 1; i <= count; i++)
    {
        if (i > 1)
            lua_pushliteral(state, "\t");
        luaL_tolstring(state, i, nullptr);
    }
    if (count == 0)
        lua_pushliteral(state, "");
    else
        lua_concat(state, count * 2 - 1);
    size_t size = 0;
    const char* text = lua_tolstring(state, -1, &size);
    km_host_log(text, size);
    return 0;
}

static int on(lua_State* state)
{
    luaL_checktype(state, 1, LUA_TSTRING);
    luaL_checktype(state, 2, LUA_TFUNCTION);
    lua_getfield(state, LUA_REGISTRYINDEX, HANDLERS);
    lua_pushvalue(state, 1);
    lua_rawget(state, -2);
    if (lua_isnil(state, -1))
        luaL_error(state, "unknown event '%s'", lua_tostring(state, 1));
    lua_pushvalue(state, 2);
    lua_rawseti(state, -2, lua_objlen(state, -2) + 1);
    return 0;
}

// kharos.system(name, fn): система мода. Её зовёт хост (km_system) раз за проход с таблицей столбцов (buffer,
// см. km_buffer) и числом сущностей. Проба: в схеме API этого пока нет.
static int system(lua_State* state)
{
    luaL_checktype(state, 1, LUA_TSTRING);
    luaL_checktype(state, 2, LUA_TFUNCTION);
    lua_getfield(state, LUA_REGISTRYINDEX, SYSTEMS);
    lua_pushvalue(state, 1);
    lua_pushvalue(state, 2);
    lua_rawset(state, -3);
    return 0;
}

static int call(lua_State* state)
{
    int id = lua_tointeger(state, lua_upvalueindex(1));
    lua_State* outer = cur;
    cur = state;
    int results = km_host_call(id, lua_gettop(state));
    cur = outer;
    if (results >= 0)
        return results;
    luaL_where(state, 1);
    lua_pushlstring(state, scratch, size_t(-results - 1));
    lua_concat(state, 2);
    lua_error(state);
}

// Буфер строк из JS: не меньше size байт. Адрес может смениться — JS берёт его заново перед каждой записью.
KM_EXPORT char* km_scratch(size_t size)
{
    if (size > scratchSize)
    {
        char* grown = (char*)realloc(scratch, size);
        if (!grown)
            return nullptr;
        scratch = grown;
        scratchSize = size;
    }
    return scratch;
}

// Заводит машину с пределом памяти memoryLimit байт. После неё — km_event и km_function, потом km_seal.
KM_EXPORT int km_create(size_t memoryLimit)
{
    limit = memoryLimit;
    L = lua_newstate(allocate, nullptr);
    if (!L)
        return 0;
    lua_callbacks(L)->interrupt = interrupt;

    static const luaL_Reg libs[] = {
        {"", luaopen_base},
        {LUA_COLIBNAME, luaopen_coroutine},
        {LUA_TABLIBNAME, luaopen_table},
        {LUA_STRLIBNAME, luaopen_string},
        {LUA_MATHLIBNAME, luaopen_math},
        {LUA_BITLIBNAME, luaopen_bit32},
        {LUA_UTF8LIBNAME, luaopen_utf8},
        {LUA_BUFFERLIBNAME, luaopen_buffer},
        {LUA_VECLIBNAME, luaopen_vector},
        {LUA_DBLIBNAME, luaopen_debug},
        {nullptr, nullptr},
    };
    for (const luaL_Reg* lib = libs; lib->func; lib++)
    {
        lua_pushcfunction(L, lib->func, nullptr);
        lua_pushstring(L, lib->name);
        lua_call(L, 1, 0);
    }
    lua_pushcfunction(L, print, "print");
    lua_setglobal(L, "print");

    lua_newtable(L);
    lua_setfield(L, LUA_REGISTRYINDEX, HANDLERS);

    lua_newtable(L);
    lua_setfield(L, LUA_REGISTRYINDEX, SYSTEMS);
    lua_newtable(L);
    lua_setfield(L, LUA_REGISTRYINDEX, BUFFERS);

    lua_newtable(L);
    lua_pushcfunction(L, on, "on");
    lua_setfield(L, -2, "on");
    lua_pushcfunction(L, system, "system");
    lua_setfield(L, -2, "system");
    lua_setglobal(L, "kharos");
    cur = L;
    return 1;
}

// Объявляет событие с именем из km_scratch (с нулём на конце): на него можно подписаться kharos.on.
KM_EXPORT void km_event()
{
    lua_getfield(L, LUA_REGISTRYINDEX, HANDLERS);
    lua_newtable(L);
    lua_setfield(L, -2, scratch);
    lua_pop(L, 1);
}

// Добавляет kharos.<имя из km_scratch> — функцию хоста номер id.
KM_EXPORT void km_function(int id)
{
    // Имя функции Luau не копирует, а показывает в ошибках аргументов: ему нужна своя память.
    char* name = strdup(scratch);
    lua_getglobal(L, "kharos");
    lua_pushinteger(L, id);
    lua_pushcclosure(L, call, name, 1);
    lua_setfield(L, -2, name);
    lua_pop(L, 1);
}

// Закрывает библиотеки на запись и заводит поток мода.
KM_EXPORT void km_seal()
{
    luaL_sandbox(L);
    T = lua_newthread(L);
    luaL_sandboxthread(T);
    cur = T;
}

// Компилирует и выполняет код мода: в km_scratch имя (с нулём на конце), сразу за ним — исходник длиной size.
KM_EXPORT int km_load(size_t nameLength, size_t size, double budget)
{
    lua_CompileOptions options = {};
    options.optimizationLevel = 1;
    options.debugLevel = 1;
    size_t bytecodeSize = 0;
    char* bytecode = luau_compile(scratch + nameLength + 1, size, &options, &bytecodeSize);
    char* chunk = (char*)malloc(nameLength + 2);
    chunk[0] = '=';
    memcpy(chunk + 1, scratch, nameLength + 1);
    int loaded = luau_load(T, chunk, bytecode, bytecodeSize, 0);
    free(chunk);
    free(bytecode);
    if (loaded != 0)
    {
        keepError(T);
        return KM_ERROR;
    }
    begin(budget);
    int status = lua_pcall(T, 0, 0, 0);
    int result = end(status, T);
    deadline = 0;
    return result;
}

// Зовёт подписчиков события с именем из km_scratch (с нулём на конце); данные события — на верху стека, их снимает.
// Ошибка одного подписчика не мешает остальным, кончился срок — остальные не зовутся. Вернёт худшее из случившегося,
// текст — первой ошибки.
KM_EXPORT int km_emit(double budget)
{
    lua_getfield(T, LUA_REGISTRYINDEX, HANDLERS);
    lua_getfield(T, -1, scratch);
    int result = KM_OK;
    if (lua_istable(T, -1))
    {
        int count = lua_objlen(T, -1);
        begin(budget);
        for (int i = 1; i <= count; i++)
        {
            lua_rawgeti(T, -1, i);
            lua_pushvalue(T, -4);
            int status = lua_pcall(T, 1, 0, 0);
            if (status == LUA_OK)
                continue;
            if (result == KM_OK)
                result = end(status, T);
            else
                lua_pop(T, 1);
            if (expired)
            {
                result = KM_TIMEOUT;
                break;
            }
        }
        deadline = 0;
    }
    lua_pop(T, 3);
    return result;
}

// Столбец номер slot размером size байт: buffer Luau, который система получает в таблице столбцов. Вернёт адрес его
// байт в памяти wasm — хост пишет туда данные перед проходом и читает после. Сборщик Luau объекты не двигает, адрес
// живёт, пока столбец не заменят. Нет памяти — 0.
KM_EXPORT void* km_buffer(int slot, size_t size)
{
    lua_getfield(L, LUA_REGISTRYINDEX, BUFFERS);
    void* data = lua_newbuffer(L, size);
    lua_rawseti(L, -2, slot);
    lua_pop(L, 1);
    return data;
}

// Проход системы с именем из km_scratch (с нулём на конце) над count сущностями.
KM_EXPORT int km_system(int count, double budget)
{
    lua_getfield(T, LUA_REGISTRYINDEX, SYSTEMS);
    lua_getfield(T, -1, scratch);
    if (!lua_isfunction(T, -1))
    {
        lua_pop(T, 2);
        keep("unknown system", 14);
        return KM_ERROR;
    }
    lua_getfield(T, LUA_REGISTRYINDEX, BUFFERS);
    lua_pushinteger(T, count);
    begin(budget);
    int status = lua_pcall(T, 2, 0, 0);
    int result = end(status, T);
    deadline = 0;
    lua_pop(T, 1);
    return result;
}

KM_EXPORT const char* km_error()
{
    length = failureLength;
    return failure;
}

KM_EXPORT size_t km_length()
{
    return length;
}

KM_EXPORT size_t km_memory()
{
    return used;
}

KM_EXPORT int km_top()
{
    return lua_gettop(cur);
}

KM_EXPORT void km_pop(int count)
{
    lua_pop(cur, count);
}

KM_EXPORT int km_type(int index)
{
    switch (lua_type(cur, index))
    {
    case LUA_TNIL:
    case LUA_TNONE:
        return KM_NIL;
    case LUA_TBOOLEAN:
        return KM_BOOLEAN;
    case LUA_TNUMBER:
    case LUA_TINTEGER:
        return KM_NUMBER;
    case LUA_TSTRING:
        return KM_STRING;
    case LUA_TTABLE:
        return KM_TABLE;
    default:
        return KM_OTHER;
    }
}

KM_EXPORT double km_number(int index)
{
    return lua_tonumber(cur, index);
}

KM_EXPORT int km_boolean(int index)
{
    return lua_toboolean(cur, index);
}

// Строка по номеру на стеке; длина — km_length. Только для KM_STRING: число lua_tolstring переделал бы в строку.
KM_EXPORT const char* km_string(int index)
{
    size_t size = 0;
    const char* text = lua_tolstring(cur, index, &size);
    length = size;
    return text;
}

// Следующая пара таблицы с номером index (ключ под значением на стеке); 0 — пар больше нет. Начинать с km_push_nil.
KM_EXPORT int km_next(int index)
{
    return lua_next(cur, index);
}

KM_EXPORT void km_push_nil()
{
    lua_pushnil(cur);
}

KM_EXPORT void km_push_boolean(int value)
{
    lua_pushboolean(cur, value);
}

KM_EXPORT void km_push_number(double value)
{
    lua_pushnumber(cur, value);
}

// Строка из km_scratch длиной size.
KM_EXPORT void km_push_string(size_t size)
{
    lua_pushlstring(cur, scratch, size);
}

KM_EXPORT void km_push_table(int array, int fields)
{
    lua_createtable(cur, array, fields);
}

// Значение с верха стека — в поле таблицы под ним; имя поля — в km_scratch, с нулём на конце.
KM_EXPORT void km_set_field()
{
    lua_setfield(cur, -2, scratch);
}

// Значение с верха стека — в таблицу под ним под номером index.
KM_EXPORT void km_set_index(int index)
{
    lua_rawseti(cur, -2, index);
}

// Таблица на верху стека — только для чтения: данные события подписчики не правят.
KM_EXPORT void km_freeze()
{
    lua_setreadonly(cur, -1, true);
}
