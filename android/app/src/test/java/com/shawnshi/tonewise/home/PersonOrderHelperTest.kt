package com.shawnshi.tonewise.home

import org.junit.Assert.assertEquals
import org.junit.Test
import org.oxycblt.musikr.PersonArtist
import org.oxycblt.musikr.PersonRole
import org.oxycblt.musikr.covers.CoverCollection

class PersonOrderHelperTest {
    private fun person(role: PersonRole, name: String) =
        PersonArtist(
            role = role,
            rawName = name,
            songs = emptySet(),
            explicitAlbums = emptySet(),
            genres = emptyList(),
            covers = CoverCollection.from(emptyList()),
        )

    @Test
    fun applyCustomOrderPreservesManualOrderAndAppendsNewPeople() {
        val bach = person(PersonRole.COMPOSER, "Johann Sebastian Bach")
        val mozart = person(PersonRole.COMPOSER, "Wolfgang Amadeus Mozart")
        val mahler = person(PersonRole.COMPOSER, "Gustav Mahler")
        val sorted = listOf(mahler, bach, mozart)

        val encoded = PersonOrderHelper.encodeUids(listOf(mozart.uid.toString(), bach.uid.toString()))
        val decoded = PersonOrderHelper.decodeUids(encoded)
        val reordered = PersonOrderHelper.applyCustomOrder(sorted, decoded)

        assertEquals(listOf(mozart, bach, mahler), reordered)
    }

    @Test
    fun applyCustomOrderReturnsSortedWhenEmptyOrNoMatches() {
        val abbado = person(PersonRole.CONDUCTOR, "Claudio Abbado")
        val kleiber = person(PersonRole.CONDUCTOR, "Carlos Kleiber")
        val sorted = listOf(abbado, kleiber)

        assertEquals(sorted, PersonOrderHelper.applyCustomOrder(sorted, emptyList()))
        assertEquals(sorted, PersonOrderHelper.applyCustomOrder(sorted, listOf("nonexistent")))
    }
}
