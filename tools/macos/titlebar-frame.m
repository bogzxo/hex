// Isolated debug-launch fixture. See docs/macos-titlebar-qa.md.
#import <AppKit/AppKit.h>
#import <objc/runtime.h>
#import <unistd.h>
#import <math.h>
#import <stdio.h>
#import <stdlib.h>
#import <string.h>
#import <dlfcn.h>
#import <mach-o/dyld.h>
#import <stdint.h>
#import <stdatomic.h>

static NSPoint pointer;
static NSUInteger buttons;
static const NSInteger tag = 71521;
static int delivered;
static BOOL direct;
static uint32_t windowID;
static double targetX, targetY;
static atomic_int sequence;
static atomic_bool dragged;

// Stable SDL2 event ABI. The debug app ships SDL's dylib without development headers.
typedef struct {
    uint32_t type, timestamp, windowID, which;
    uint8_t button, state, clicks, padding;
    int32_t x, y;
} ButtonEvent;
typedef struct {
    uint32_t type, timestamp, windowID, which, state;
    int32_t x, y, xrel, yrel;
} MotionEvent;
typedef union { uint32_t type; ButtonEvent button; MotionEvent motion; } MouseEvent;

static int observe(void *context, MouseEvent *event)
{
    if (event->type == 0x400 && event->motion.windowID == windowID) {
        MotionEvent m = event->motion;
        fprintf(stderr, "SDL motion window=%u x=%d y=%d buttons=%u\n", m.windowID, m.x, m.y, m.state);
        if ((m.state & 1) && atomic_load(&sequence) == 1) atomic_store(&dragged, 1);
    } else if (event->type == 0x401 || event->type == 0x402) {
        ButtonEvent b = event->button;
        BOOL down = event->type == 0x401;
        fprintf(stderr, "SDL %s window=%u x=%d y=%d button=%u clicks=%u\n",
            down ? "down" : "up", b.windowID, b.x, b.y, b.button, b.clicks);
        if (b.windowID == windowID && b.button == 1) {
            if (down && fabs(b.x - targetX) <= 2 && fabs(b.y - targetY) <= 2)
                atomic_store(&sequence, 1);
            else if (!down && atomic_load(&sequence) == 1)
                atomic_store(&sequence, 2);
        }
    }
    return 1;
}

static void installWatch(double x, double y)
{
    // Resolve against the instance already driving this process, including RTLD_LOCAL loads.
    for (uint32_t i = 0; i < _dyld_image_count(); i++) {
        const char *path = _dyld_get_image_name(i);
        if (!path || !strcasestr(path, "sdl")) continue;
        void *lib = dlopen(path, RTLD_NOLOAD | RTLD_LAZY);
        if (!lib) continue;
        void (*add)(int (*)(void *, MouseEvent *), void *) = dlsym(lib, "SDL_AddEventWatch");
        void *(*focus)(void) = dlsym(lib, "SDL_GetKeyboardFocus");
        uint32_t (*identify)(void *) = dlsym(lib, "SDL_GetWindowID");
        if (add && focus && identify && focus()) {
            windowID = identify(focus());
            targetX = x;
            targetY = y;
            add(observe, NULL);
            fprintf(stderr, "SDL watch window=%u target=(%.1f,%.1f) library=%s\n", windowID, x, y, path);
            return;
        }
        dlclose(lib);
    }
    fprintf(stderr, "TITLEBAR ERROR: could not attach to the focused SDL2 window\n");
    _exit(2);
}
static void later(double seconds, dispatch_block_t block)
{
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(seconds * NSEC_PER_SEC)),
                   dispatch_get_main_queue(), block);
}

static void post(NSWindow *window, NSEventType type)
{
    NSEvent *event = [NSEvent mouseEventWithType:type
        location:[window convertPointFromScreen:pointer] modifierFlags:0
        timestamp:NSProcessInfo.processInfo.systemUptime windowNumber:window.windowNumber
        context:nil eventNumber:tag clickCount:1 pressure:buttons ? 1 : 0];
    if (direct) [NSApp sendEvent:event];
    else [NSApp postEvent:event atStart:NO];
}

static void run(void)
{
    char mode[16], extra;
    double x, y;
    const char *args = getenv("HEX_TITLEBAR_PROBE");
    NSWindow *window = NSApp.keyWindow;
    if (!args || sscanf(args, "%15[^,],%lf,%lf%c", mode, &x, &y, &extra) != 3 ||
        (strcmp(mode, "caption") && strcmp(mode, "control")) || !window.isVisible ||
        !isfinite(x) || !isfinite(y) || !NSPointInRect(NSMakePoint(x,y), window.contentView.bounds)) {
        fprintf(stderr, "TITLEBAR ERROR: expected a focused window and caption,X,Y or control,X,Y inside it\n");
        _exit(2);
    }
    BOOL moving = !strcmp(mode, "caption");
    direct = getenv("HEX_TITLEBAR_SEND_DIRECT") && !strcmp(getenv("HEX_TITLEBAR_SEND_DIRECT"), "1");
    installWatch(x, y);
    NSRect before = window.frame;
    if (moving && !NSContainsRect(window.screen.frame, NSOffsetRect(before, 64, 0))) {
        fprintf(stderr, "TITLEBAR ERROR: frame %s lacks room to move right on screen %s\n",
            NSStringFromRect(before).UTF8String, NSStringFromRect(window.screen.frame).UTF8String);
        _exit(2);
    }
    NSView *view = window.contentView;
    NSPoint local = NSMakePoint(NSMinX(view.bounds) + x,
        view.isFlipped ? NSMinY(view.bounds) + y : NSMaxY(view.bounds) - y);
    pointer = [window convertPointToScreen:[view convertPoint:local toView:nil]];
    // SDL Cocoa reads these two NSEvent class methods for SDL_GetGlobalMouseState.
    // Override only inside this explicitly selected, disposable test process.
    method_setImplementation(class_getClassMethod(NSEvent.class, @selector(mouseLocation)),
        imp_implementationWithBlock(^NSPoint(id receiver) { return pointer; }));
    method_setImplementation(class_getClassMethod(NSEvent.class, @selector(pressedMouseButtons)),
        imp_implementationWithBlock(^NSUInteger(id receiver) { return buttons; }));
    [NSEvent addLocalMonitorForEventsMatchingMask:NSEventMaskMouseMoved | NSEventMaskLeftMouseDown |
        NSEventMaskLeftMouseDragged | NSEventMaskLeftMouseUp handler:^NSEvent *(NSEvent *event) {
            if (event.window == window && event.eventNumber == tag) delivered++;
            return event;
        }];
    post(window, NSEventTypeMouseMoved);
    later(.25, ^{ buttons = 1; post(window, NSEventTypeLeftMouseDown); });
    later(.60, ^{ pointer.x += 32; post(window, NSEventTypeLeftMouseDragged); });
    later(.85, ^{ pointer.x += 32; post(window, NSEventTypeLeftMouseDragged); });
    later(1.15, ^{ buttons = 0; post(window, NSEventTypeLeftMouseUp); });
    later(1.55, ^{
        NSRect after = window.frame;
        BOOL ok = atomic_load(&sequence) == 2 && atomic_load(&dragged) && fabs(after.origin.x - before.origin.x - (moving ? 64 : 0)) <= 1
            && fabs(after.origin.y - before.origin.y) <= 1 && NSEqualSizes(before.size, after.size);
        fprintf(stderr, "TITLEBAR %s simulated %s via %s, AppKit monitor=%d, SDL button sequence=%d, SDL drag=%d, scale=%.2f, frame %s -> %s\n",
            ok ? "PASS" : "FAIL", moving ? "caption" : "control", direct ? "sendEvent" : "postEvent",
            delivered, atomic_load(&sequence), atomic_load(&dragged), window.backingScaleFactor,
            NSStringFromRect(before).UTF8String, NSStringFromRect(after).UTF8String);
        _exit(ok ? 0 : 1);
    });
}

__attribute__((constructor)) static void start(void)
{
    const char *pid = getenv("HEX_TITLEBAR_PID");
    if (!pid || atoi(pid) != getpid()) return;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 30 * NSEC_PER_SEC),
        dispatch_get_global_queue(QOS_CLASS_DEFAULT, 0), ^{
            fprintf(stderr, "TITLEBAR ERROR: timed out waiting for the window event loop\n");
            _exit(2);
        });
    later(8, ^{ run(); });
}
