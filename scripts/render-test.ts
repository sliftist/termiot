// Terminal throughput test: renders a spinning 3D torus with per-cell 256-color ANSI escapes, repainting the full screen every frame. Run with `yarn typenode scripts/render-test.ts`. Optional args: --frames N (exit after N frames and print stats), --fps N (target frame rate), --no-color (plain ASCII, far fewer bytes per frame).

const DEFAULT_TARGET_FPS = 60;
const TORUS_RING_RADIUS = 2;
const TORUS_TUBE_RADIUS = 1;
const CAMERA_DISTANCE = 5;
const THETA_STEPS = 90;
const PHI_STEPS = 240;
const ROTATION_A_PER_FRAME = 0.04;
const ROTATION_B_PER_FRAME = 0.02;
const CELL_ASPECT_RATIO = 0.5;
const LUMINANCE_CHARS = ".,-~:;=!*#$@";
const STATS_INTERVAL_FRAMES = 30;

interface CliArgs {
    frames?: number;
    targetFps: number;
    color: boolean;
}

function parseArgs(argv: string[]): CliArgs {
    const args: CliArgs = { targetFps: DEFAULT_TARGET_FPS, color: true };
    for (let i = 0; i < argv.length; i++) {
        if (argv[i] === "--frames") {
            args.frames = Number(argv[++i]);
        } else if (argv[i] === "--fps") {
            args.targetFps = Number(argv[++i]);
        } else if (argv[i] === "--no-color") {
            args.color = false;
        }
    }
    return args;
}

const GRAYSCALE_RAMP_START = 232;
const GRAYSCALE_RAMP_END = 255;
const HUE_COLORS = [39, 45, 51, 50, 49, 48, 47, 46, 82, 118, 154, 190];

function colorFor(luminance: number, phase: number): number {
    if (luminance < 0.15) {
        const idx = GRAYSCALE_RAMP_START + Math.floor(luminance / 0.15 * (GRAYSCALE_RAMP_END - GRAYSCALE_RAMP_START));
        return idx;
    }
    return HUE_COLORS[Math.floor(phase * HUE_COLORS.length) % HUE_COLORS.length];
}

function sleep(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}

async function main(): Promise<void> {
    const args = parseArgs(process.argv.slice(2));
    const out = process.stdout;

    out.write("\x1b[?1049h\x1b[?25l");
    const restore = () => out.write("\x1b[?25h\x1b[?1049l");
    process.on("SIGINT", () => {
        restore();
        process.exit(0);
    });
    process.on("exit", restore);

    let rotA = 0;
    let rotB = 0;
    let frameCount = 0;
    let bytesWritten = 0;
    const startTime = Date.now();
    let windowStartTime = startTime;
    let fpsDisplay = 0;

    const frameBudgetMs = 1000 / args.targetFps;

    while (true) {
        const frameStart = Date.now();
        const cols = out.columns ?? 80;
        const rows = out.rows ?? 24;

        const zBuffer = new Float32Array(cols * rows);
        const lumBuffer = new Float32Array(cols * rows).fill(-1);
        const phaseBuffer = new Float32Array(cols * rows);

        const cosA = Math.cos(rotA), sinA = Math.sin(rotA);
        const cosB = Math.cos(rotB), sinB = Math.sin(rotB);
        const scale = Math.min(cols * CELL_ASPECT_RATIO, rows) * CAMERA_DISTANCE / (8 * (TORUS_RING_RADIUS + TORUS_TUBE_RADIUS)) * 3;

        for (let t = 0; t < THETA_STEPS; t++) {
            const theta = t / THETA_STEPS * Math.PI * 2;
            const cosT = Math.cos(theta), sinT = Math.sin(theta);
            const circleX = TORUS_RING_RADIUS + TORUS_TUBE_RADIUS * cosT;
            const circleY = TORUS_TUBE_RADIUS * sinT;
            for (let p = 0; p < PHI_STEPS; p++) {
                const phi = p / PHI_STEPS * Math.PI * 2;
                const cosP = Math.cos(phi), sinP = Math.sin(phi);

                const x = circleX * (cosB * cosP + sinA * sinB * sinP) - circleY * cosA * sinB;
                const y = circleX * (sinB * cosP - sinA * cosB * sinP) + circleY * cosA * cosB;
                const z = CAMERA_DISTANCE + cosA * circleX * sinP + circleY * sinA;
                const invZ = 1 / z;

                const px = Math.floor(cols / 2 + scale * invZ * x);
                const py = Math.floor(rows / 2 - scale * invZ * y * CELL_ASPECT_RATIO);
                if (px < 0 || px >= cols || py < 0 || py >= rows) continue;

                const lum = cosP * cosT * sinB - cosA * cosT * sinP - sinA * sinT + cosB * (cosA * sinT - cosT * sinA * sinP);
                const idx = py * cols + px;
                if (invZ > zBuffer[idx]) {
                    zBuffer[idx] = invZ;
                    lumBuffer[idx] = lum;
                    phaseBuffer[idx] = p / PHI_STEPS;
                }
            }
        }

        let frame = "\x1b[H";
        let lastColor = -1;
        for (let row = 0; row < rows; row++) {
            if (row > 0) frame += "\r\n";
            for (let col = 0; col < cols; col++) {
                const idx = row * cols + col;
                const lum = lumBuffer[idx];
                if (lum < 0) {
                    if (args.color && lastColor !== 0) {
                        frame += "\x1b[0m";
                        lastColor = 0;
                    }
                    frame += " ";
                    continue;
                }
                const clamped = Math.max(0, Math.min(0.999, (lum + 1) / 2));
                const ch = LUMINANCE_CHARS[Math.floor(clamped * LUMINANCE_CHARS.length)];
                if (args.color) {
                    const color = colorFor(clamped, phaseBuffer[idx]);
                    if (color !== lastColor) {
                        frame += `\x1b[38;5;${color}m`;
                        lastColor = color;
                    }
                }
                frame += ch;
            }
        }
        frame += "\x1b[0m";
        lastColor = -1;

        const elapsed = (Date.now() - startTime) / 1000;
        if (frameCount > 0 && frameCount % STATS_INTERVAL_FRAMES === 0) {
            const windowElapsed = (Date.now() - windowStartTime) / 1000;
            fpsDisplay = STATS_INTERVAL_FRAMES / windowElapsed;
            windowStartTime = Date.now();
        }
        const mib = bytesWritten / (1024 * 1024);
        frame += `\x1b[H\x1b[7m frame ${frameCount}  ${fpsDisplay.toFixed(1)} fps  ${mib.toFixed(1)} MiB written  ${cols}x${rows}  ${elapsed.toFixed(0)}s \x1b[0m`;

        bytesWritten += Buffer.byteLength(frame);
        const flushed = out.write(frame);
        if (!flushed) {
            await new Promise<void>(resolve => out.once("drain", resolve));
        }

        frameCount++;
        rotA += ROTATION_A_PER_FRAME;
        rotB += ROTATION_B_PER_FRAME;

        if (args.frames !== undefined && frameCount >= args.frames) {
            restore();
            const totalSeconds = (Date.now() - startTime) / 1000;
            console.log(`Rendered ${frameCount} frames in ${totalSeconds.toFixed(2)}s = ${(frameCount / totalSeconds).toFixed(1)} fps average, ${(bytesWritten / (1024 * 1024)).toFixed(1)} MiB written (${(bytesWritten / (1024 * 1024) / totalSeconds).toFixed(1)} MiB/s)`);
            process.exit(0);
        }

        const frameTime = Date.now() - frameStart;
        const wait = frameBudgetMs - frameTime;
        if (wait > 0) {
            await sleep(wait);
        }
    }
}

void main();
