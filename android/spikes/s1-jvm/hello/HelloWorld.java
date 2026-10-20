// S1 spike 用最小验证类：验证 JVM 拉起后能正确执行 main 并产生标准输出
public class HelloWorld {
    public static void main(String[] args) {
        int n = (args == null) ? 0 : args.length;
        System.out.println("Hello from Starcloud S1 spike, java.version="
            + System.getProperty("java.version") + ", args=" + n);
    }
}
