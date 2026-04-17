using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Party;

public class PartyIdGeneratorTests
{
    [Fact]
    public void Generate_Returns6CharactersFromAllowedAlphabet()
    {
        for (int i = 0; i < 200; i++)
        {
            var id = PartyIdGenerator.Generate();
            Assert.Equal(6, id.Length);
            foreach (var c in id) Assert.Contains(c, PartyIdGenerator.Alphabet);
        }
    }

    [Fact]
    public void Alphabet_DoesNotContainConfusableCharacters()
    {
        Assert.DoesNotContain('0', PartyIdGenerator.Alphabet);
        Assert.DoesNotContain('O', PartyIdGenerator.Alphabet);
        Assert.DoesNotContain('1', PartyIdGenerator.Alphabet);
        Assert.DoesNotContain('I', PartyIdGenerator.Alphabet);
    }
}
