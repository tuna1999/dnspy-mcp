using System.Threading.Tasks;

namespace TestNS;

public class TestClass {
    public int TestMethod() => 42;
    public async Task<int> AsyncMethod() => await Task.FromResult(1);
    public T GenericMethod<T>(T input) => input;
}

public abstract class AbstractBase {
    public abstract void DoWork();
}

public interface IInterface {
    void Run();
}

public enum TestEnum {
    Zero = 0,
    One = 1,
    Two = 2,
}
