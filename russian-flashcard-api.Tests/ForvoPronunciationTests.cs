using Xunit;
using russian_flashcard_api;

namespace russian_flashcard_api.Tests;

public class ForvoPronunciationTests
{
    [Fact]
    public void Constructor_CreatesDefaultInstance()
    {
        // Arrange & Act
        var pronunciation = new ForvoPronunciation();
        
        // Assert
        Assert.NotNull(pronunciation);
        Assert.Equal(string.Empty, pronunciation.AudioMp3);
        Assert.Equal(string.Empty, pronunciation.Sex);
        Assert.Equal(string.Empty, pronunciation.Username);
        Assert.Equal(string.Empty, pronunciation.Country);
    }

    [Fact]
    public void Constructor_CanSetProperties()
    {
        // Arrange & Act
        var pronunciation = new ForvoPronunciation
        {
            AudioMp3 = "http://example.com/audio.mp3",
            Sex = "male",
            Username = "user123",
            Country = "ru"
        };
        
        // Assert
        Assert.Equal("http://example.com/audio.mp3", pronunciation.AudioMp3);
        Assert.Equal("male", pronunciation.Sex);
        Assert.Equal("user123", pronunciation.Username);
        Assert.Equal("ru", pronunciation.Country);
    }

    [Fact]
    public void AudioMp3Property_CanBeModified()
    {
        // Arrange
        var pronunciation = new ForvoPronunciation();
        
        // Act
        pronunciation.AudioMp3 = "http://example.com/new.mp3";
        
        // Assert
        Assert.Equal("http://example.com/new.mp3", pronunciation.AudioMp3);
    }

    [Fact]
    public void SexProperty_CanBeModified()
    {
        // Arrange
        var pronunciation = new ForvoPronunciation();
        
        // Act
        pronunciation.Sex = "female";
        
        // Assert
        Assert.Equal("female", pronunciation.Sex);
    }

    [Fact]
    public void UsernameProperty_CanBeModified()
    {
        // Arrange
        var pronunciation = new ForvoPronunciation();
        
        // Act
        pronunciation.Username = "testuser";
        
        // Assert
        Assert.Equal("testuser", pronunciation.Username);
    }

    [Fact]
    public void CountryProperty_CanBeModified()
    {
        // Arrange
        var pronunciation = new ForvoPronunciation();
        
        // Act
        pronunciation.Country = "Russia";
        
        // Assert
        Assert.Equal("Russia", pronunciation.Country);
    }
}
