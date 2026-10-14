import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';

const here = path.dirname(fileURLToPath(import.meta.url));
const destination = path.resolve(here, '../../src/PairUp.App/Web/strands');
const vendor = path.join(destination, 'vendor');
const generated = path.join(here, '.generated');
const source = await fs.readFile(path.join(vendor, 'Strands.jsx'), 'utf8');
const pinnedSourceHash = '52901975284c1536114a899f6e03494088ef12085f4f1a8931bbd34f4c8006e9';
if (crypto.createHash('sha256').update(source).digest('hex') !== pinnedSourceHash) throw new Error('Pinned upstream Strands source changed. Do not silently rebuild changed vendor code.');
let adapted = source;
function replaceOnce(before, after) {
  if (adapted.split(before).length !== 2) throw new Error('Strands upstream changed; review adaptation: ' + before.slice(0, 60));
  adapted = adapted.replace(before, after);
}
// Only lifecycle, size observation and viewport mapping are adapted. All three shaders and the
// original uniform updates, drawing order, palette, and blend mode are retained.
replaceOnce("  className = '',", "  paused = false,\n  className = '',");
replaceOnce('    glassSize\n  };', '    glassSize,\n    paused\n  };');
replaceOnce('  const ctnDom = useRef(null);', '  const ctnDom = useRef(null);\n  const controlsRef = useRef(null);');
replaceOnce('    const gl = renderer.gl;', `    const gl = renderer.gl;
    // Capability/link checks are lifecycle guards. Upstream shader text stays intact.
    function failInitialization(message, programs = [], geometry = null, target = null) {
      // Delete allocated objects even when WEBGL_lose_context is unavailable.
      const clean = task => { try { task(); } catch (_) { /* Continue releasing the remaining objects. */ } };
      for (const item of programs) {
        if (!item) continue;
        clean(() => gl.deleteProgram(item.program));
        clean(() => gl.deleteShader(item.vertexShader));
        clean(() => gl.deleteShader(item.fragmentShader));
      }
      if (geometry) clean(() => geometry.remove());
      if (target) {
        for (const texture of target.textures || []) clean(() => gl.deleteTexture(texture.texture));
        if (target.depthTexture) clean(() => gl.deleteTexture(target.depthTexture.texture));
        for (const key of ['depthBuffer', 'stencilBuffer', 'depthStencilBuffer']) if (target[key]) clean(() => gl.deleteRenderbuffer(target[key]));
        clean(() => gl.deleteFramebuffer(target.buffer));
      }
      if (gl?.canvas?.parentNode === ctn) ctn.removeChild(gl.canvas);
      clean(() => gl?.getExtension('WEBGL_lose_context')?.loseContext());
      throw new Error(message);
    }
    if (!renderer.isWebgl2) failInitialization('Strands requires WebGL 2; the decorative effect is unavailable.');`);
replaceOnce('    const mesh = new Mesh(gl, { geometry, program });', `    if (!gl.getProgramParameter(program.program, gl.LINK_STATUS)) {
      failInitialization('Strands shader program did not link.', [program], geometry);
    }
    const mesh = new Mesh(gl, { geometry, program });`);
replaceOnce('      delete geometry.attributes.uv;', '      gl.deleteBuffer(geometry.attributes.uv.buffer);\n      delete geometry.attributes.uv;');
replaceOnce('    const glassMesh = new Mesh(gl, { geometry, program: glassProgram });', `    if (!gl.getProgramParameter(glassProgram.program, gl.LINK_STATUS)) {
      failInitialization('Strands glass shader program did not link.', [program, glassProgram], geometry, renderTarget);
    }
    const glassMesh = new Mesh(gl, { geometry, program: glassProgram });`);
replaceOnce('      const width = ctn.offsetWidth;\n      const height = ctn.offsetHeight;', `      // The original shader normalizes x by canvas height. A fixed logical
      // aspect lets the entire wave map onto the chosen physical width, rather
      // than adding empty space when only the container gets wider. The sphere
      // always uses a square logical frame and a square physical host.
      const logicalAspect = propsRef.current.glass ? 1 : 1.7;
      const displayWidth = Math.max(1, ctn.offsetWidth);
      const displayHeight = Math.max(1, ctn.offsetHeight);
      const height = Math.ceil(Math.max(displayHeight, displayWidth / logicalAspect));
      const width = Math.max(1, Math.round(height * logicalAspect));`);
replaceOnce('      renderer.setSize(width, height);', `      renderer.setSize(width, height);
      // OGL writes inline pixel dimensions. Map its logical image to the exact
      // user-controlled display box without modifying any shader or recipe.
      gl.canvas.style.width = '100%';
      gl.canvas.style.height = '100%';`);
replaceOnce('      glassProgram.uniforms.uResolution.value = [width, height];\n    }', '      glassProgram.uniforms.uResolution.value = [width, height];\n      if (propsRef.current.paused) controlsRef.current?.sync();\n    }');
replaceOnce("    window.addEventListener('resize', resize);", "    window.addEventListener('resize', resize);\n    const resizeObserver = new ResizeObserver(resize);\n    resizeObserver.observe(ctn);");
replaceOnce('      animateId = requestAnimationFrame(update);', '      animateId = propsRef.current.paused ? 0 : requestAnimationFrame(update);');
replaceOnce('      program.uniforms.uTime.value = t * 0.001;', '      program.uniforms.uTime.value = current.paused ? 0 : t * 0.001;');
replaceOnce('    animateId = requestAnimationFrame(update);\n\n    return () => {', '    controlsRef.current = { resize, sync() { cancelAnimationFrame(animateId); animateId = requestAnimationFrame(update); } };\n    controlsRef.current.sync();\n\n    return () => {');
replaceOnce("      window.removeEventListener('resize', resize);", "      window.removeEventListener('resize', resize);\n      resizeObserver.disconnect();\n      controlsRef.current = null;");
replaceOnce('  }, []);\n\n  return', '  }, []);\n\n  useEffect(() => { controlsRef.current?.resize(); }, [glass]);\n  useEffect(() => { controlsRef.current?.sync(); }, [paused, colors, count, speed, amplitude, waviness, thickness, glow, taper, spread, hueShift, intensity, saturation, opacity, scale, glass, refraction, dispersion, glassSize]);\n\n  return');

function shaders(text) {
  return Object.fromEntries([...text.matchAll(/const (VERT|FRAG|GLASS_FRAG) = `([\s\S]*?)`;/g)].map(match => [match[1], crypto.createHash('sha256').update(match[2]).digest('hex')]));
}
const originalShaders = shaders(source);
if (Object.keys(originalShaders).length !== 3 || JSON.stringify(originalShaders) !== JSON.stringify(shaders(adapted))) throw new Error('Original Strands shaders must remain byte-identical.');
await fs.mkdir(generated, { recursive: true });
await fs.writeFile(path.join(generated, 'Strands.jsx'), adapted);
await fs.copyFile(path.join(vendor, 'Strands.css'), path.join(generated, 'Strands.css'));
await build({
  entryPoints: [path.join(destination, 'entry.jsx')],
  outfile: path.join(destination, 'strands-runtime.js'),
  bundle: true,
  minify: true,
  format: 'iife',
  platform: 'browser',
  target: ['chrome110'],
  jsx: 'automatic',
  define: { 'process.env.NODE_ENV': '"production"' },
  alias: { '@shengyu/strands-adapted': path.join(generated, 'Strands.jsx') },
  nodePaths: [path.join(here, 'node_modules')],
  legalComments: 'eof',
  banner: { js: '/* React Bits Strands — David Haz, MIT + Commons Clause. See vendor/LICENSE-react-bits.md and SOURCE-NOTICE.md. */' }
});
const licenses = ['react', 'react-dom', 'scheduler'];
for (const name of licenses) await fs.copyFile(path.join(here, 'node_modules', name, 'LICENSE'), path.join(destination, 'LICENSE-' + name + '.txt'));
// OGL 1.0.11 publishes its complete Unlicense in README.md, not a LICENSE file.
const oglReadme = await fs.readFile(path.join(here, 'node_modules/ogl/README.md'), 'utf8');
const oglLicense = oglReadme.slice(oglReadme.indexOf('## Unlicense'));
if (!oglLicense.startsWith('## Unlicense') || !oglLicense.includes('THE SOFTWARE IS PROVIDED')) throw new Error('Missing OGL license');
await fs.writeFile(path.join(destination, 'LICENSE-ogl.txt'), oglLicense);
const manifest = {
  upstream: 'https://github.com/DavidHDev/react-bits',
  commit: '496968c4117e21278f13917201b7d615bc5e7450',
  sourceSha256: crypto.createHash('sha256').update(source).digest('hex'),
  shaders: originalShaders,
  dependencies: JSON.parse(await fs.readFile(path.join(here, 'package.json'), 'utf8')).dependencies,
  buildTool: { esbuild: '0.25.11' },
  viewportMapping: { waveLogicalAspect: 1.7, sphereLogicalAspect: 1, display: 'CSS maps the logical image to a user-controlled wave rectangle or a square sphere region without changing recipe values.' },
  adaptations: ['Optional static frame for reduced motion', 'Pause or unmount when hidden', 'ResizeObserver with nonzero dimensions', 'Release frame, observer, canvas and WebGL context on unmount', 'Require WebGL 2 and successful links for both shader programs before scheduling frames', 'Explicitly delete allocated graphics objects and release context after initialization failure', 'Map a fixed logical wave aspect onto the physical layout and keep both sphere aspects square'],
  rendererAlgorithm: 'Original VERT, FRAG and GLASS_FRAG are byte-identical; recipe uniform updates and render passes are retained. Resolution follows the documented viewport mapping.'
};
await fs.writeFile(path.join(destination, 'build-manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
console.log('Strands built locally; all 3 original shader hashes match.');
