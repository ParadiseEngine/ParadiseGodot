shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

// Procedural ball material used by scenes/pool.tscn. `kind` (1..9) selects a look; the two colors
// plus noise scale, flow speed, emission, and transmission drive it — the Godot-side counterpart of
// the engine's HDR procedural ball materials (lava/jade/ice/marble …), so both hosts render textured
// balls from the same authored parameters.
//
// NOTE: reconstructed to match the shader-parameter interface authored in pool.tscn (color_a, color_b,
// emissive_strength, flow_speed, noise_scale, transmission, kind). The original source shader was
// never committed (it lived only under the gitignored data/ tree), so a fresh checkout of pool.tscn
// failed to load — this restores it. The look is functional, not a byte-match of the original.

uniform int kind = 0;
uniform vec4 color_a : source_color = vec4(1.0);
uniform vec4 color_b : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform float emissive_strength = 0.0;
uniform float flow_speed = 0.0;
uniform float noise_scale = 1.0;
uniform float transmission = 0.0;

// Cheap 3D value noise + fbm (object-space, so the pattern is stable on a rolling ball).
float hash(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float vnoise(vec3 x) {
	vec3 i = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(hash(i + vec3(0.0, 0.0, 0.0)), hash(i + vec3(1.0, 0.0, 0.0)), f.x),
				   mix(hash(i + vec3(0.0, 1.0, 0.0)), hash(i + vec3(1.0, 1.0, 0.0)), f.x), f.y),
			   mix(mix(hash(i + vec3(0.0, 0.0, 1.0)), hash(i + vec3(1.0, 0.0, 1.0)), f.x),
				   mix(hash(i + vec3(0.0, 1.0, 1.0)), hash(i + vec3(1.0, 1.0, 1.0)), f.x), f.y), f.z);
}

float fbm(vec3 p) {
	float a = 0.5;
	float s = 0.0;
	for (int i = 0; i < 4; i++) {
		s += a * vnoise(p);
		p *= 2.0;
		a *= 0.5;
	}
	return s;
}

varying vec3 v_obj;

void vertex() {
	v_obj = VERTEX;
}

void fragment() {
	vec3 p = v_obj * max(noise_scale, 0.001) + vec3(0.0, TIME * flow_speed, 0.0);
	float n = fbm(p);
	float t = n;

	// Three families across the nine kinds so the balls read differently: cloudy, veined, speckled.
	int family = kind % 3;
	if (family == 1) {
		// veined / marble: sharp bands warped by the noise
		t = abs(sin(v_obj.y * max(noise_scale, 0.001) * 3.0 + n * 4.0));
	} else if (family == 2) {
		// speckled: high-contrast spots
		t = smoothstep(0.45, 0.55, n);
	}

	vec3 col = mix(color_a.rgb, color_b.rgb, clamp(t, 0.0, 1.0));
	ALBEDO = col;
	METALLIC = 0.0;
	ROUGHNESS = clamp(0.55 - transmission * 0.35, 0.05, 1.0);
	EMISSION = col * emissive_strength;
}
