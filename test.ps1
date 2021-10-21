BeforeAll {
    Import-Module $PSScriptRoot\RecordEmitter\bin\Debug\net6.0\RecordEmitter.dll
}

Describe "test-records" {
    It "Returns expected output" {
        $recordMembers = [System.Collections.Generic.List[RecordEmitter.RETyper+RecordMember]]::new()
        @(
            [RecordEmitter.RETyper+RecordMember]::new([int], 'ID')
            [RecordEmitter.RETyper+RecordMember]::new([string], 'Name')
        ) |% { $recordMembers.Add($_) }
        
        $recordType = [RecordEmitter.RETyper]::CreateRecordStruct("MyRecordStructType__$(New-Guid)", $recordMembers)

        $john = $recordType::new(123, "John")
        $john2 = $recordType::new(123, "John")
        $john |Should -Be $john2

        $rob = $recordType::new(145, "Rob")
        $rob | Should -Not -Be $john
    }
}
