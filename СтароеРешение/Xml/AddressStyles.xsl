<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:ct="urn:BimHouse:CommonDataType" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:cfg="urn:BimHouse:AddressUtilConfig" xmlns:msxsl="urn:schemas-microsoft-com:xslt" xmlns:bf='urn:BimHouse:XslFunctions'>
	<xsl:import href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/BimHouseFunctions.xsl"/>
	<xsl:output method="xml" version="1.0" encoding="UTF-8" indent="yes"/>
	<cfg:Config>
		<TagPrefixes>
			<TagPrefix key="ct:Улица" value="ул.&#160;"/>
			<TagPrefix key="ct:Дом" value="д.&#160;"/>
			<TagPrefix key="ct:Помещение" value="оф.&#160;"/>
			<TagPrefix key="ct:Телефон" value="тел.:&#160;"/>
			<TagPrefix key="ct:Email" value="e&#8209;mail:&#160;"/>
			<TagPrefix key="ct:Сайт" value="web:&#160;"/>
		</TagPrefixes>
	</cfg:Config>

	<!--<xsl:template match="*[starts-with(@xsi:type,'ct:Тип.Базовый.Адрес')]" mode="presenting">-->
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.Базовый.Адрес')]" mode="presenting">
		<xsl:element name="{name()}" namespace="{namespace-uri()}">
			<xsl:copy-of select="@*"/>
			<xsl:apply-templates select="./node()" mode="#current"/>
			<xsl:element name="ct:Представления" namespace="urn:BimHouse:CommonDataType">
				<xsl:element name="ct:ПолныйАдрес" namespace="urn:BimHouse:CommonDataType">
					<xsl:call-template name="CreateAddressTextpresentingType1">
						<xsl:with-param name="Address" select="."/>
					</xsl:call-template>
				</xsl:element>
				<xsl:element name="ct:Контакты" namespace="urn:BimHouse:CommonDataType">
					<xsl:call-template name="CreateAddressTextpresentingType2">
						<xsl:with-param name="Address" select="."/>
					</xsl:call-template>
				</xsl:element>
			</xsl:element>
		</xsl:element>
	</xsl:template>

	<xsl:template name="CreateAddressTextpresentingType1">
		<xsl:param name="Address"/>
		<xsl:for-each select="$Address/ct:Индекс | $Address/ct:Страна | $Address/ct:Регион | $Address/ct:Район | $Address/ct:НаселённыйПункт | $Address/ct:Улица | $Address/ct:Дом | $Address/ct:Помещение">
			<xsl:if test="position() != 1">
				<xsl:text>, </xsl:text>
			</xsl:if>
			<xsl:variable name="ElementName" select="name()"/>
			<xsl:value-of select="document('')/*/cfg:Config/TagPrefixes/TagPrefix[@key = $ElementName]/@value"/>
			<xsl:value-of select="."/>
		</xsl:for-each>
		<xsl:if test="$Address/ct:Координаты">
			<xsl:text>, коорд.:&#160;</xsl:text>
			<xsl:value-of select="$Address/ct:Координаты/ct:Широта/ct:Градусы"/>
			<xsl:choose>
				<xsl:when test="$Address/ct:Координаты/ct:Широта/@ГрадусыСимвол != ''">
					<xsl:value-of select="$Address/ct:Координаты/ct:Широта/@ГрадусыСимвол"/>
				</xsl:when>
				<xsl:otherwise>
					<xsl:text>°</xsl:text>
				</xsl:otherwise>
			</xsl:choose>
			<xsl:if test="$Address/ct:Координаты/ct:Широта/ct:Минуты != ''">
				<xsl:text>&#160;</xsl:text>
				<xsl:value-of select="$Address/ct:Координаты/ct:Широта/ct:Минуты"/>
				<xsl:choose>
					<xsl:when test="$Address/ct:Координаты/ct:Широта/@МинутыСимвол != ''">
						<xsl:value-of select="$Address/ct:Координаты/ct:Широта/@МинутыСимвол"/>
					</xsl:when>
					<xsl:otherwise>
						<xsl:text>'</xsl:text>
					</xsl:otherwise>
				</xsl:choose>
			</xsl:if>
			<xsl:if test="$Address/ct:Координаты/ct:Широта/ct:Секунды != ''">
				<xsl:text>&#160;</xsl:text>
				<xsl:value-of select="$Address/ct:Координаты/ct:Широта/ct:Секунды"/>
				<xsl:choose>
					<xsl:when test="$Address/ct:Координаты/ct:Широта/@СекундыСимвол != ''">
						<xsl:value-of select="$Address/ct:Координаты/ct:Широта/@СекундыСимвол"/>
					</xsl:when>
					<xsl:otherwise>
						<xsl:text>"</xsl:text>
					</xsl:otherwise>
				</xsl:choose>
			</xsl:if>
			<xsl:text>, </xsl:text>
			<xsl:value-of select="$Address/ct:Координаты/ct:Долгота/ct:Градусы"/>
			<xsl:choose>
				<xsl:when test="$Address/ct:Координаты/ct:Долгота/@ГрадусыСимвол != ''">
					<xsl:value-of select="$Address/ct:Координаты/ct:Долгота/@ГрадусыСимвол"/>
				</xsl:when>
				<xsl:otherwise>
					<xsl:text>°</xsl:text>
				</xsl:otherwise>
			</xsl:choose>
			<xsl:if test="$Address/ct:Координаты/ct:Долгота/ct:Минуты != ''">
				<xsl:text>&#160;</xsl:text>
				<xsl:value-of select="$Address/ct:Координаты/ct:Долгота/ct:Минуты"/>
				<xsl:choose>
					<xsl:when test="$Address/ct:Координаты/ct:Долгота/@МинутыСимвол != ''">
						<xsl:value-of select="$Address/ct:Координаты/ct:Долгота/@МинутыСимвол"/>
					</xsl:when>
					<xsl:otherwise>
						<xsl:text>'</xsl:text>
					</xsl:otherwise>
				</xsl:choose>
			</xsl:if>
			<xsl:if test="$Address/ct:Координаты/ct:Долгота/ct:Секунды != ''">
				<xsl:text>&#160;</xsl:text>
				<xsl:value-of select="$Address/ct:Координаты/ct:Долгота/ct:Секунды"/>
				<xsl:choose>
					<xsl:when test="$Address/ct:Координаты/ct:Долгота/@СекундыСимвол != ''">
						<xsl:value-of select="$Address/ct:Координаты/ct:Долгота/@СекундыСимвол"/>
					</xsl:when>
					<xsl:otherwise>
						<xsl:text>"</xsl:text>
					</xsl:otherwise>
				</xsl:choose>
			</xsl:if>
		</xsl:if>
	</xsl:template>
	<xsl:template name="CreateAddressTextpresentingType2">
		<xsl:param name="Address"/>
		<xsl:for-each select="$Address/ct:Телефон | $Address/ct:Сайт | $Address/ct:Email">
			<xsl:if test="position() != 1">
				<xsl:text>, </xsl:text>
			</xsl:if>
			<xsl:variable name="ElementName" select="name()"/>
			<xsl:value-of select="document('')/*/cfg:Config/TagPrefixes/TagPrefix[@key = $ElementName]/@value"/>
			<xsl:value-of select="."/>
		</xsl:for-each>
	</xsl:template>
</xsl:stylesheet>