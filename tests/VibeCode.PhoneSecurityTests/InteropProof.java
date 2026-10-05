import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.spec.ECGenParameterSpec;
import java.util.Base64;
import java.util.HexFormat;

// JCA uses the same SHA256withECDSA / DER signature and SPKI public-key formats as Android DeviceIdentity.
// No production key or credential is used. This proves wire compatibility, not physical Keystore behavior.
class InteropProof {
    public static void main(String[] args) throws Exception {
        var generator = KeyPairGenerator.getInstance("EC");
        generator.initialize(new ECGenParameterSpec("secp256r1"));
        var key = generator.generateKeyPair();
        var body = "{\"text\":\"hello \\uD83D\\uDD10\"}";
        var token = "isolated-interop-token";
        var target = "/api/chats/abc/send?v=1&wait=25";
        var nonce = "0123456789abcdef0123456789abcdef";
        var canonical = String.join("\n", "vibecode-device-v1", args[0], args[1], nonce,
            "POST", target, hash(body), hash(token));
        var signer = Signature.getInstance("SHA256withECDSA");
        signer.initSign(key.getPrivate());
        signer.update(canonical.getBytes(StandardCharsets.UTF_8));
        System.out.println(Base64.getEncoder().encodeToString(key.getPublic().getEncoded()));
        System.out.println(Base64.getEncoder().encodeToString(signer.sign()));
        System.out.println(Base64.getEncoder().encodeToString(body.getBytes(StandardCharsets.UTF_8)));
    }
    private static String hash(String value) throws Exception {
        return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8)));
    }
}
